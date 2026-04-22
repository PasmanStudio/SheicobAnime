using AnimeIndex.Api.Data;
using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Api.Infrastructure.Scraping;
using AnimeIndex.Scraper.Infrastructure;
using AnimeIndex.Scraper.Strategies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AnimeIndex.Scraper.Jobs;

/// <summary>
/// Two-pass backfill job for full historical scrape — pure HTTP, no Playwright.
/// Pass 1: Crawl all directory pages to discover every series slug.
/// Pass 2: For each series needing enrichment, scrape detail + episodes + mirrors.
/// Supports resume from last progress on restart.
/// </summary>
public class BackfillJob(
    AppDbContext db,
    MirrorProbeService probe,
    UpsertPipelineService upsert,
    JkAnimeHttpClient http,
    DeadLetterAlerter alerter,
    IConfiguration config,
    ILogger<BackfillJob> logger)
{
    public async Task ExecuteAsync(Guid scrapeJobId, int maxPages, CancellationToken ct = default)
    {
        var job = await db.ScrapeJobs.FindAsync([scrapeJobId], ct);
        if (job is null)
        {
            logger.LogError("Backfill job {JobId} not found", scrapeJobId);
            return;
        }

        job.Status = "running";
        await db.SaveChangesAsync(ct);

        var sourceKey = job.JobType.StartsWith("backfill:")
            ? job.JobType["backfill:".Length..]
            : "source2";

        var baseUrl = config[$"{char.ToUpper(sourceKey[0])}{sourceKey[1..]}:BaseUrl"]
            ?? config["Source2:BaseUrl"]
            ?? "https://jkanime.net";
        var delayMs = config.GetValue($"{char.ToUpper(sourceKey[0])}{sourceKey[1..]}:DelayMs", 1500);

        // Parse resume state from ErrorMessage
        var resumeState = ParseProgress(job.ErrorMessage);
        var startPage = resumeState.LastCompletedPage + 1;

        logger.LogInformation(
            "Backfill starting (HTTP mode) — source={Source}, maxPages={MaxPages}, resumeFrom={StartPage}",
            sourceKey, maxPages, startPage);

        try
        {
            // ═══════════════════════════════════════════════
            // PASS 1: Discover all series slugs from directory pages
            // ═══════════════════════════════════════════════
            var discoveredSeries = new List<(string Slug, int? AnimeId)>();
            var newSeriesCount = 0;
            var skippedExisting = 0;

            var existingSlugs = await db.Series
                .AsNoTracking()
                .Select(s => s.Slug)
                .ToListAsync(ct);
            var existingSlugSet = new HashSet<string>(existingSlugs, StringComparer.OrdinalIgnoreCase);

            for (var page = startPage; page <= maxPages; page++)
            {
                if (ct.IsCancellationRequested) break;
                if (await http.CheckCircuitBreakerAsync(ct))
                    logger.LogWarning("Circuit breaker tripped during discovery — paused 10 min");

                var directoryPage = await http.GetDirectoryPageAsync(baseUrl, page, ct);

                if (directoryPage is null)
                {
                    // Retry once
                    await JkAnimeHttpClient.JitterDelayAsync(delayMs * 2, ct);
                    directoryPage = await http.GetDirectoryPageAsync(baseUrl, page, ct);
                    if (directoryPage is null)
                    {
                        logger.LogWarning("Cannot reach directory page {Page} on retry — ending pass 1", page);
                        break;
                    }
                }

                if (directoryPage.Data.Count == 0) break;

                foreach (var item in directoryPage.Data)
                {
                    if (string.IsNullOrWhiteSpace(item.Slug) || string.IsNullOrWhiteSpace(item.Title))
                        continue;

                    var slug = item.Slug.Trim();

                    var blocked = await db.BlockedSlugs.AnyAsync(b => b.Slug == slug, ct);
                    if (blocked) continue;

                    discoveredSeries.Add((slug, item.Id > 0 ? item.Id : null));

                    if (existingSlugSet.Contains(slug))
                    {
                        skippedExisting++;
                        continue;
                    }

                    var status = MapStatus(item.Status);
                    var type = MapType(item.Type);

                    var data = new SeriesScrapedData(
                        Slug: slug,
                        Title: item.Title.Trim(),
                        CoverUrl: item.Image,
                        Status: status,
                        Type: type,
                        Synopsis: string.IsNullOrWhiteSpace(item.Synopsis) ? null : item.Synopsis.Trim());

                    await upsert.UpsertSeriesAsync(data, ct);
                    existingSlugSet.Add(slug);
                    newSeriesCount++;
                }

                logger.LogDebug("Pass 1 — page {Page}/{MaxPages}: {New} new, {Skipped} existing",
                    page, maxPages, newSeriesCount, skippedExisting);

                if (page % 5 == 0)
                    await UpdateProgressAsync(job, $"pass1:page:{page}/{maxPages},new:{newSeriesCount},skipped:{skippedExisting}", ct);

                await JkAnimeHttpClient.JitterDelayAsync(delayMs, ct);
            }

            await UpdateProgressAsync(job, $"pass1:complete,new:{newSeriesCount},skipped:{skippedExisting},starting:pass2", ct);
            logger.LogInformation("Pass 1 complete — {New} new series, {Skipped} existing skipped", newSeriesCount, skippedExisting);

            // ═══════════════════════════════════════════════
            // PASS 2: Enrich each series (detail + episodes + mirrors)
            // ═══════════════════════════════════════════════
            var enrichedCount = 0;
            var episodeTotal = 0;
            var mirrorTotal = 0;
            var skippedCount = 0;

            var seriesToEnrich = await db.Series
                .AsNoTracking()
                .Where(s => s.Synopsis == null
                    || s.LastScrapedAt == null
                    || s.LastScrapedAt < DateTime.UtcNow.AddHours(-24)
                    || !db.Episodes.Any(e => e.SeriesId == s.Id
                        && db.Mirrors.Any(m => m.EpisodeId == e.Id)))
                .OrderBy(s => s.LastScrapedAt ?? DateTime.MinValue)
                .Select(s => new { s.Id, s.Slug })
                .ToListAsync(ct);

            var alreadyEnrichedSlugs = resumeState.EnrichedSlugs;

            // Build a lookup for anime IDs from pass 1 discovery
            var animeIdBySlug = discoveredSeries
                .Where(d => d.AnimeId.HasValue)
                .GroupBy(d => d.Slug, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().AnimeId!.Value, StringComparer.OrdinalIgnoreCase);

            logger.LogInformation("Pass 2 — {Count} series to enrich (skipping {Enriched} already done)",
                seriesToEnrich.Count, alreadyEnrichedSlugs.Count);

            var failedCount = 0;

            foreach (var series in seriesToEnrich)
            {
                if (ct.IsCancellationRequested) break;

                if (alreadyEnrichedSlugs.Contains(series.Slug))
                {
                    skippedCount++;
                    continue;
                }

                if (await http.CheckCircuitBreakerAsync(ct))
                    logger.LogWarning("Circuit breaker tripped during enrichment — paused 10 min");

                try
                {
                    // Scrape series detail page
                    var detail = await http.GetSeriesDetailAsync(baseUrl, series.Slug, ct);
                    if (detail is not null)
                    {
                        var enrichedData = new SeriesScrapedData(
                            Slug: series.Slug,
                            Title: detail.Title,
                            CoverUrl: detail.CoverUrl,
                            Status: detail.Status ?? "ongoing",
                            Type: detail.Type ?? "tv",
                            Synopsis: detail.Synopsis,
                            Year: detail.Year,
                            Genres: detail.Genres);
                        await upsert.UpsertSeriesAsync(enrichedData, ct);
                    }

                    // Get anime ID for AJAX episodes endpoint
                    var animeId = detail?.AnimeId
                        ?? (animeIdBySlug.TryGetValue(series.Slug, out var cached) ? cached : (int?)null);

                    if (animeId is null)
                    {
                        logger.LogDebug("No anime ID for {Slug} — skipping episodes", series.Slug);
                        enrichedCount++;
                        continue;
                    }

                    // Discover episodes via AJAX
                    var episodes = await http.GetAllEpisodesAsync(baseUrl, animeId.Value, series.Slug, ct);

                    foreach (var ep in episodes)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (ep.Number <= 0 || ep.Number > short.MaxValue) continue;
                        var epNum = (short)ep.Number;

                        var epData = new EpisodeScrapedData(
                            SeriesId: series.Id,
                            EpisodeNumber: epNum,
                            Title: null,
                            PendingMirrors: []);

                        var episodeId = await upsert.UpsertEpisodeAsync(epData, ct);
                        episodeTotal++;

                        // Extract mirrors from episode page
                        var epUrl = $"{baseUrl.TrimEnd('/')}/{series.Slug}/{epNum}/";
                        var mirrorUrls = await http.GetEpisodeMirrorUrlsAsync(epUrl, ct);

                        foreach (var url in mirrorUrls)
                        {
                            if (IsJkAnimeDomain(url)) continue;
                            var providerName = Source2Strategy.ExtractProviderName(url);
                            if (BlockedProviders.Contains(providerName)) continue;

                            var embeddable = await probe.IsEmbeddableAsync(url, ct);
                            if (!embeddable) continue;

                            await upsert.UpsertMirrorAsync(new MirrorScrapedData(
                                EpisodeId: episodeId,
                                ProviderName: providerName,
                                EmbedUrl: url,
                                QualityLabel: 720,
                                Priority: GetProviderPriority(providerName)), ct);
                            mirrorTotal++;
                        }

                        await JkAnimeHttpClient.JitterDelayAsync(delayMs, ct);
                    }

                    await upsert.SyncEpisodeCountAsync(series.Id, ct);

                    enrichedCount++;
                    logger.LogDebug("Enriched {Slug}: {EpCount} episodes, {MirrorCount} mirrors total",
                        series.Slug, episodeTotal, mirrorTotal);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failedCount++;
                    logger.LogWarning(ex, "Failed to enrich {Slug} — skipping (failures={Failed})", series.Slug, failedCount);
                }

                if ((enrichedCount + failedCount) % 5 == 0)
                {
                    await UpdateProgressAsync(job,
                        $"pass2:enriched:{enrichedCount}/{seriesToEnrich.Count},episodes:{episodeTotal},mirrors:{mirrorTotal},failed:{failedCount}",
                        ct);
                }
            }

            await UpdateProgressAsync(job,
                $"complete:new:{newSeriesCount},enriched:{enrichedCount},episodes:{episodeTotal},mirrors:{mirrorTotal},failed:{failedCount}",
                ct);

            logger.LogInformation(
                "Backfill complete — new={S}, enriched={E}, episodes={Ep}, mirrors={M}, skipped={Sk}, failed={F}",
                newSeriesCount, enrichedCount, episodeTotal, mirrorTotal, skippedCount, failedCount);

            await alerter.HandleSuccessAsync(scrapeJobId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backfill job {JobId} failed", scrapeJobId);
            await alerter.HandleFailureAsync(scrapeJobId, ex.Message, ct);
            throw;
        }
    }

    // ── Progress persistence ────────────────────────────────

    private async Task UpdateProgressAsync(ScrapeJob job, string progress, CancellationToken ct)
    {
        job.ErrorMessage = $"{{\"maxPages\":{ParseProgress(job.ErrorMessage).MaxPages},\"progress\":\"{progress}\"}}";
        db.Entry(job).Property(j => j.ErrorMessage).IsModified = true;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Backfill progress: {Progress}", progress);
    }

    private static BackfillProgress ParseProgress(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new BackfillProgress(200, 0, []);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var maxPages = root.TryGetProperty("maxPages", out var mp) ? mp.GetInt32() : 200;
            var progress = root.TryGetProperty("progress", out var p) ? p.GetString() ?? "" : "";

            var lastPage = 0;
            if (progress.StartsWith("pass1:page:"))
            {
                var pageStr = progress["pass1:page:".Length..];
                var slashIdx = pageStr.IndexOf('/');
                if (slashIdx > 0 && int.TryParse(pageStr[..slashIdx], out var pg))
                    lastPage = pg;
            }
            else if (progress.Contains("pass1:complete") || progress.StartsWith("pass2:") || progress.StartsWith("complete:"))
            {
                lastPage = maxPages;
            }

            var enrichedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return new BackfillProgress(maxPages, lastPage, enrichedSlugs);
        }
        catch
        {
            return new BackfillProgress(200, 0, []);
        }
    }

    private sealed record BackfillProgress(int MaxPages, int LastCompletedPage, HashSet<string> EnrichedSlugs);

    // ── Helpers ─────────────────────────────────────────────

    private static bool IsJkAnimeDomain(string url) =>
        url.Contains("jkanime.net", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("jkdesa.com", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> BlockedProviders = new(StringComparer.OrdinalIgnoreCase) { "mega", "mediafire" };

    private static readonly Dictionary<string, short> ProviderPriorities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["okru"] = 1, ["mp4upload"] = 2, ["sendvid"] = 3, ["yourupload"] = 4,
        ["filemoon"] = 5, ["streamwish"] = 6, ["voe"] = 7, ["vidhide"] = 8,
        ["streamtape"] = 9, ["fembed"] = 10, ["doodstream"] = 11, ["nozomi"] = 12,
        ["mixdrop"] = 13, ["desu"] = 14,
    };

    private static short GetProviderPriority(string provider) =>
        ProviderPriorities.TryGetValue(provider, out var p) ? p : (short)50;

    private static string MapStatus(string? raw) => raw switch
    {
        "currently" => "ongoing",
        "completed" => "completed",
        "notyet" => "upcoming",
        _ => "ongoing"
    };

    private static string MapType(string? raw) => raw?.ToLowerInvariant() switch
    {
        "tv" => "tv", "movie" => "movie", "ova" => "ova",
        "ona" => "ona", "special" => "special", _ => "tv"
    };
}
