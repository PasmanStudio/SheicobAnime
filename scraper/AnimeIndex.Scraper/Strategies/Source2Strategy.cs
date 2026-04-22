using AnimeIndex.Api.Data;
using AnimeIndex.Api.Infrastructure.Scraping;
using AnimeIndex.Scraper.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Strategies;

/// <summary>
/// JKAnime scraper (jkanime.net) — pure HTTP, no Playwright/Chromium.
/// Browse /directorio with pagination → series detail → episode AJAX → episode page mirrors.
/// </summary>
public sealed class Source2Strategy(
    AppDbContext db,
    MirrorProbeService probe,
    UpsertPipelineService upsert,
    JkAnimeHttpClient http,
    IConfiguration config,
    ILogger<Source2Strategy> logger) : IScrapeStrategy
{
    public string SourceKey => "source2";

    public async Task<ScrapeResult> ScrapeAsync(Guid scrapeJobId, CancellationToken ct = default)
    {
        var baseUrl = config["Source2:BaseUrl"]
            ?? throw new InvalidOperationException("Source2:BaseUrl is not configured.");
        var delayMs = config.GetValue("Source2:DelayMs", 1500);
        var maxPages = config.GetValue("Source2:MaxPages", 3);

        var seriesCount = 0;
        var episodeCount = 0;
        var mirrorCount = 0;
        var skippedCount = 0;
        var noEpisodesCount = 0;

        // ── Check blocked_slugs for this job's series ────────
        var job = await db.ScrapeJobs
            .Include(j => j.Series)
            .FirstOrDefaultAsync(j => j.Id == scrapeJobId, ct);

        if (job is null)
            return new ScrapeResult(false, $"ScrapeJob {scrapeJobId} not found.");

        string? targetSlug = job.Series?.Slug;
        if (targetSlug is not null)
        {
            var isBlocked = await db.BlockedSlugs
                .AnyAsync(b => b.Slug == targetSlug, ct);
            if (isBlocked)
            {
                logger.LogWarning("Slug {Slug} is blocked — skipping", targetSlug);
                return new ScrapeResult(false, $"Slug '{targetSlug}' is in blocked_slugs.");
            }
        }

        logger.LogInformation("JKAnimeStrategy starting (HTTP mode) — baseUrl={BaseUrl}, maxPages={MaxPages}", baseUrl, maxPages);

        // Helper: write heartbeat + progress to the scrape_jobs row so admins
        // can see live progress, and the scheduler can distinguish alive vs stuck.
        const int HeartbeatInterval = 10; // every N series
        var totalDiscovered = 0;
        async Task WriteHeartbeatAsync()
        {
            try
            {
                // Use raw SQL to avoid EF change tracker conflicts
                var msg = $"series:{seriesCount}/{totalDiscovered} eps:{episodeCount} mirrors:{mirrorCount} skipped:{skippedCount} noEps:{noEpisodesCount}";
                await db.Database.ExecuteSqlRawAsync(
                    """UPDATE scrape_jobs SET progress_message = {0}, last_heartbeat = now() WHERE id = {1}""",
                    msg, scrapeJobId);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to write heartbeat for job {JobId}", scrapeJobId);
            }
        }

        // Initialize heartbeat
        await WriteHeartbeatAsync();

        // ── Discover series from /directorio ──────────────────
        var discovered = await DiscoverSeriesAsync(baseUrl, maxPages, delayMs, scrapeJobId, ct);
        totalDiscovered = discovered.Count;

        foreach (var (series, animeId) in discovered)
        {
            if (ct.IsCancellationRequested) break;

            if (await http.CheckCircuitBreakerAsync(ct))
                logger.LogWarning("Circuit breaker tripped — paused 10 min");

            var blocked = await db.BlockedSlugs.AnyAsync(b => b.Slug == series.Slug, ct);
            if (blocked)
            {
                logger.LogDebug("Skipping blocked slug {Slug}", series.Slug);
                continue;
            }

            // ── Smart skip logic ────────────────────────────────
            var existing = await db.Series
                .Where(s => s.Slug == series.Slug)
                .Select(s => new
                {
                    s.Id,
                    s.Status,
                    DbEpisodeCount = s.Episodes.Count,
                    EpisodesWithMirrors = s.Episodes.Count(e =>
                        e.Mirrors.Any(m => m.IsActive)),
                })
                .FirstOrDefaultAsync(ct);

            var isCompleted = series.Status == "completed"
                              || existing is { Status: "completed" };
            var hasAllEps = existing is { DbEpisodeCount: > 0 }
                            && existing.EpisodesWithMirrors == existing.DbEpisodeCount;

            if (isCompleted && hasAllEps)
            {
                logger.LogDebug(
                    "Skipping fully-indexed completed series {Slug} — {Eps} episodes, all with mirrors",
                    series.Slug, existing!.DbEpisodeCount);
                seriesCount++;
                skippedCount++;
                if (seriesCount % HeartbeatInterval == 0)
                    await WriteHeartbeatAsync();
                continue;
            }

            // Navigate to series detail page for enrichment
            var detail = await http.GetSeriesDetailAsync(baseUrl, series.Slug, ct);
            var enriched = detail is not null
                ? series with
                {
                    Title = detail.Title,
                    Synopsis = detail.Synopsis ?? series.Synopsis,
                    CoverUrl = detail.CoverUrl ?? series.CoverUrl,
                    Status = detail.Status ?? series.Status,
                    Type = detail.Type ?? series.Type,
                    Year = detail.Year,
                    Genres = detail.Genres ?? series.Genres,
                    TitleRomaji = detail.TitleEnglish,
                    TitleNative = detail.TitleJapanese,
                    EpisodeCount = detail.EpisodeCount ?? series.EpisodeCount,
                    Studio = detail.Studio,
                    Season = detail.Season,
                    Demographics = detail.Demographics,
                    Language = detail.Language,
                    DurationMinutes = detail.DurationMinutes,
                    AiredDate = detail.AiredDate,
                    Quality = detail.Quality,
                }
                : series;

            var seriesId = await upsert.UpsertSeriesAsync(enriched, ct);
            seriesCount++;

            // ── Discover episodes ─────────────────────────────
            var effectiveAnimeId = detail?.AnimeId ?? animeId;
            if (effectiveAnimeId is null)
            {
                logger.LogWarning("No anime ID for {Slug} — skipping episode discovery (detail={DetailNull})",
                    series.Slug, detail is null ? "null" : "ok");
                noEpisodesCount++;
                if (seriesCount % HeartbeatInterval == 0)
                    await WriteHeartbeatAsync();
                await JkAnimeHttpClient.JitterDelayAsync(delayMs, ct);
                continue;
            }

            var episodes = await http.GetAllEpisodesAsync(baseUrl, effectiveAnimeId.Value, series.Slug, ct);

            if (episodes.Count == 0)
            {
                logger.LogWarning("Zero episodes returned for {Slug} (animeId={AnimeId}) — AJAX may have failed",
                    series.Slug, effectiveAnimeId.Value);
                noEpisodesCount++;
            }

            // Pre-load episode numbers that already have active mirrors → skip those
            var episodesWithMirrors = existing is not null
                ? await db.Episodes
                    .Where(e => e.SeriesId == seriesId && e.Mirrors.Any(m => m.IsActive))
                    .Select(e => e.EpisodeNumber)
                    .ToHashSetAsync(ct)
                : new HashSet<short>();

            foreach (var ep in episodes)
            {
                if (ct.IsCancellationRequested) break;

                if (ep.Number <= 0 || ep.Number > short.MaxValue) continue;
                var epNum = (short)ep.Number;

                var epData = new EpisodeScrapedData(
                    SeriesId: seriesId,
                    EpisodeNumber: epNum,
                    Title: null,
                    PendingMirrors: []);

                var episodeId = await upsert.UpsertEpisodeAsync(epData, ct);
                episodeCount++;

                // Skip mirror scraping for episodes that already have active mirrors
                if (episodesWithMirrors.Contains(epNum))
                {
                    logger.LogDebug("Episode {Slug} ep{Num}: already has mirrors — skipping",
                        series.Slug, epNum);
                    continue;
                }

                // Fetch episode page for mirror extraction
                var epUrl = $"{baseUrl.TrimEnd('/')}/{series.Slug}/{epNum}/";
                var mirrorUrls = await http.GetEpisodeMirrorUrlsAsync(epUrl, ct);

                foreach (var url in mirrorUrls)
                {
                    if (IsJkAnimeDomain(url)) continue;

                    var providerName = ExtractProviderName(url);
                    if (BlockedProviders.Contains(providerName)) continue;

                    var embeddable = await probe.IsEmbeddableAsync(url, ct);
                    if (!embeddable)
                    {
                        logger.LogDebug("Mirror {Url} not embeddable — skipped", url);
                        continue;
                    }

                    var mirror = new MirrorScrapedData(
                        EpisodeId: episodeId,
                        ProviderName: providerName,
                        EmbedUrl: url,
                        QualityLabel: 720,
                        Priority: GetProviderPriority(providerName));

                    await upsert.UpsertMirrorAsync(mirror, ct);
                    mirrorCount++;
                }

                await JkAnimeHttpClient.JitterDelayAsync(delayMs, ct);
            }

            // Sync episode count from actual DB records
            await upsert.SyncEpisodeCountAsync(seriesId, ct);

            // Periodic heartbeat
            if (seriesCount % HeartbeatInterval == 0)
                await WriteHeartbeatAsync();
        }

        logger.LogInformation(
            "JKAnimeStrategy complete (HTTP mode) — series={S} episodes={E} mirrors={M} skipped={K} noEps={N}",
            seriesCount, episodeCount, mirrorCount, skippedCount, noEpisodesCount);

        // Final heartbeat
        await WriteHeartbeatAsync();

        if (seriesCount == 0 && episodeCount == 0)
            return new ScrapeResult(false,
                "Zero series and episodes indexed — JKAnime may be unreachable or has changed structure.");

        return new ScrapeResult(true,
            SeriesIndexed: seriesCount,
            EpisodesIndexed: episodeCount,
            MirrorsIndexed: mirrorCount);
    }

    // ── Directory browsing ──────────────────────────────────

    private async Task<IReadOnlyList<(SeriesScrapedData Series, int? AnimeId)>> DiscoverSeriesAsync(
        string baseUrl, int maxPages, int delayMs, Guid scrapeJobId, CancellationToken ct)
    {
        var result = new List<(SeriesScrapedData, int?)>();

        for (var page = 1; page <= maxPages; page++)
        {
            if (ct.IsCancellationRequested) break;
            if (await http.CheckCircuitBreakerAsync(ct)) break;

            // Write heartbeat every 10 pages during discovery
            if (page % 10 == 0)
            {
                try
                {
                    var msg = $"discovering page {page}/{maxPages} ({result.Count} series so far)";
                    await db.Database.ExecuteSqlRawAsync(
                        """UPDATE scrape_jobs SET progress_message = {0}, last_heartbeat = now() WHERE id = {1}""",
                        msg, scrapeJobId);
                }
                catch { /* best-effort */ }
            }

            var directoryPage = await http.GetDirectoryPageAsync(baseUrl, page, ct);
            if (directoryPage?.Data is null || directoryPage.Data.Count == 0)
                break;

            foreach (var item in directoryPage.Data)
            {
                if (string.IsNullOrWhiteSpace(item.Slug) || string.IsNullOrWhiteSpace(item.Title))
                    continue;

                var status = item.Status switch
                {
                    "currently" => "ongoing",
                    "completed" => "completed",
                    "notyet" => "upcoming",
                    _ => "ongoing"
                };

                var type = item.Type?.ToLowerInvariant() switch
                {
                    "tv" => "tv",
                    "movie" => "movie",
                    "ova" => "ova",
                    "ona" => "ona",
                    "special" => "special",
                    _ => "tv"
                };

                result.Add((new SeriesScrapedData(
                    Slug: item.Slug.Trim(),
                    Title: item.Title.Trim(),
                    CoverUrl: item.Image,
                    Status: status,
                    Type: type,
                    Synopsis: string.IsNullOrWhiteSpace(item.Synopsis) ? null : item.Synopsis.Trim()),
                    item.Id > 0 ? item.Id : null));
            }

            logger.LogDebug("Page {Page}: discovered {Count} series", page, directoryPage.Data.Count);
            await JkAnimeHttpClient.JitterDelayAsync(delayMs, ct);
        }

        return result;
    }

    // ── Provider helpers ────────────────────────────────────

    private static bool IsJkAnimeDomain(string url) =>
        url.Contains("jkanime.net", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("jkdesa.com", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> BlockedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "mega", "mediafire",
    };

    private static readonly Dictionary<string, short> ProviderPriorities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["okru"] = 1, ["mp4upload"] = 2, ["sendvid"] = 3, ["yourupload"] = 4,
        ["filemoon"] = 5, ["streamwish"] = 6, ["voe"] = 7, ["vidhide"] = 8,
        ["streamtape"] = 9, ["fembed"] = 10, ["doodstream"] = 11, ["nozomi"] = 12,
        ["mixdrop"] = 13, ["desu"] = 14,
    };

    private static short GetProviderPriority(string provider) =>
        ProviderPriorities.TryGetValue(provider, out var p) ? p : (short)50;

    public static string ExtractProviderName(string url)
    {
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host[4..];

            return host switch
            {
                var h when h.Contains("fembed") || h.Contains("fplayer") => "fembed",
                var h when h.Contains("streamtape") => "streamtape",
                var h when h.Contains("mp4upload") => "mp4upload",
                var h when h.Contains("yourupload") => "yourupload",
                var h when h.Contains("dood") => "doodstream",
                var h when h.Contains("ok.ru") => "okru",
                var h when h.Contains("sendvid") => "sendvid",
                var h when h.Contains("vidlox") => "vidlox",
                var h when h.Contains("mixdrop") => "mixdrop",
                var h when h.Contains("filemoon") => "filemoon",
                var h when h.Contains("streamwish") || h.Contains("fastwish") || h.Contains("swish") || h.Contains("bysekoze") => "streamwish",
                var h when h.Contains("voe") => "voe",
                var h when h.Contains("desu") || h.Contains("playmudos") => "desu",
                var h when h.Contains("nozomi") => "nozomi",
                var h when h.Contains("mega") => "mega",
                var h when h.Contains("vidhide") || h.Contains("dsvplay") => "vidhide",
                var h when h.Contains("mxdrop") => "mixdrop",
                _ => host.Split('.')[0]
            };
        }
        catch
        {
            return "unknown";
        }
    }
}
