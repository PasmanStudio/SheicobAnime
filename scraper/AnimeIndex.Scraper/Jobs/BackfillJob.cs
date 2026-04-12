using AnimeIndex.Api.Data;
using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Api.Infrastructure.Scraping;
using AnimeIndex.Scraper.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using System.Text.Json;

namespace AnimeIndex.Scraper.Jobs;

/// <summary>
/// Two-pass backfill job for full historical scrape.
/// Pass 1: Crawl all directory pages to discover every series slug.
/// Pass 2: For each series needing enrichment, scrape detail + episodes + mirrors.
/// Supports resume from last progress on restart.
/// </summary>
public class BackfillJob(
    AppDbContext db,
    MirrorProbeService probe,
    UpsertPipelineService upsert,
    DeadLetterAlerter alerter,
    IConfiguration config,
    ILogger<BackfillJob> logger) : PlaywrightBase
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
            "Backfill starting — source={Source}, maxPages={MaxPages}, resumeFrom={StartPage}",
            sourceKey, maxPages, startPage);

        try
        {
            await using var _ = this;
            await InitializeAsync();

            // ═══════════════════════════════════════════════
            // PASS 1: Discover all series slugs from directory pages
            // Only upsert series that don't exist yet (skip existing ones).
            // ═══════════════════════════════════════════════
            var discoveredSlugs = new List<string>();
            var newSeriesCount = 0;
            var skippedExisting = 0;

            // Pre-load existing slugs to avoid unnecessary upserts
            var existingSlugs = await db.Series
                .AsNoTracking()
                .Select(s => s.Slug)
                .ToListAsync(ct);
            var existingSlugSet = new HashSet<string>(existingSlugs, StringComparer.OrdinalIgnoreCase);

            for (var page = startPage; page <= maxPages; page++)
            {
                if (ct.IsCancellationRequested) break;
                if (await CheckCircuitBreakerAsync(ct))
                    logger.LogWarning("Circuit breaker tripped during discovery — paused 10 min");

                var url = $"{baseUrl.TrimEnd('/')}/directorio?p={page}";
                if (!await GoToAsync(url, ct))
                {
                    logger.LogWarning("Cannot reach {Url} — retrying once", url);
                    await JitterDelayAsync(delayMs * 2, ct);
                    if (!await GoToAsync(url, ct))
                    {
                        logger.LogWarning("Cannot reach {Url} on retry — ending pass 1 at page {Page}", url, page);
                        break;
                    }
                }

                // Extract JSON from `var animes = {...}`
                string? animesJson = null;
                try
                {
                    await Page.WaitForFunctionAsync(
                        "typeof animes !== 'undefined' && animes.data && animes.data.length > 0",
                        new PageWaitForFunctionOptions { Timeout = 10_000 });
                    animesJson = await Page.EvaluateAsync<string>("JSON.stringify(animes)");
                }
                catch (TimeoutException)
                {
                    logger.LogDebug("No animes data on page {Page} — end of directory", page);
                    break;
                }

                if (string.IsNullOrWhiteSpace(animesJson))
                    break;

                var animesPage = JsonSerializer.Deserialize<JkDirectoryPage>(animesJson);
                if (animesPage?.Data is null || animesPage.Data.Count == 0)
                    break;

                foreach (var item in animesPage.Data)
                {
                    if (string.IsNullOrWhiteSpace(item.Slug) || string.IsNullOrWhiteSpace(item.Title))
                        continue;

                    var slug = item.Slug.Trim();

                    // Check blocked slugs
                    var blocked = await db.BlockedSlugs.AnyAsync(b => b.Slug == slug, ct);
                    if (blocked) continue;

                    // Track the slug for Pass 2 consideration
                    discoveredSlugs.Add(slug);

                    // Only upsert if series doesn't exist yet — skip existing ones
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

                // Update progress every 5 pages
                if (page % 5 == 0)
                    await UpdateProgressAsync(job, $"pass1:page:{page}/{maxPages},new:{newSeriesCount},skipped:{skippedExisting}", ct);

                await JitterDelayAsync(delayMs, ct);
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

            // Get all series that need enrichment:
            //  - Never enriched (no synopsis, never scraped)
            //  - Not scraped in the last 24h
            //  - Has no mirrors at all (needs mirror extraction regardless of last_scraped_at)
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

            // Also include series from resume state that may not have been enriched yet
            var alreadyEnrichedSlugs = resumeState.EnrichedSlugs;

            logger.LogInformation("Pass 2 — {Count} series to enrich (skipping {Enriched} already done)",
                seriesToEnrich.Count, alreadyEnrichedSlugs.Count);

            foreach (var series in seriesToEnrich)
            {
                if (ct.IsCancellationRequested) break;

                // Skip if already enriched in this run (resume support)
                if (alreadyEnrichedSlugs.Contains(series.Slug))
                {
                    skippedCount++;
                    continue;
                }

                if (await CheckCircuitBreakerAsync(ct))
                    logger.LogWarning("Circuit breaker tripped during enrichment — paused 10 min");

                // Scrape series detail page
                var detail = await ScrapeSeriesDetailAsync(baseUrl, series.Slug, delayMs, ct);
                if (detail is not null)
                {
                    await upsert.UpsertSeriesAsync(detail, ct);
                }

                // Discover episodes
                var episodes = await ScrapeEpisodesFromDetailAsync(baseUrl, series.Slug, series.Id, delayMs, ct);

                foreach (var ep in episodes)
                {
                    if (ct.IsCancellationRequested) break;

                    var episodeId = await upsert.UpsertEpisodeAsync(ep, ct);
                    episodeTotal++;

                    // Extract mirrors from episode page
                    var epUrl = $"{baseUrl.TrimEnd('/')}/{series.Slug}/{ep.EpisodeNumber}/";
                    var mirrors = await ScrapeEpisodeMirrorsAsync(epUrl, episodeId, delayMs, ct);

                    foreach (var mirror in mirrors)
                    {
                        var embeddable = await probe.IsEmbeddableAsync(mirror.EmbedUrl, ct);
                        if (!embeddable) continue;

                        await upsert.UpsertMirrorAsync(mirror, ct);
                        mirrorTotal++;
                    }

                    await JitterDelayAsync(delayMs, ct);
                }

                // Sync episode count from actual DB records
                await upsert.SyncEpisodeCountAsync(series.Id, ct);

                enrichedCount++;

                // Update progress every 10 series
                if (enrichedCount % 10 == 0)
                {
                    await UpdateProgressAsync(job,
                        $"pass2:enriched:{enrichedCount}/{seriesToEnrich.Count},episodes:{episodeTotal},mirrors:{mirrorTotal}",
                        ct);
                }
            }

            await UpdateProgressAsync(job,
                $"complete:new:{newSeriesCount},enriched:{enrichedCount},episodes:{episodeTotal},mirrors:{mirrorTotal}",
                ct);

            logger.LogInformation(
                "Backfill complete — new={S}, enriched={E}, episodes={Ep}, mirrors={M}, skipped={Sk}",
                newSeriesCount, enrichedCount, episodeTotal, mirrorTotal, skippedCount);

            await alerter.HandleSuccessAsync(scrapeJobId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backfill job {JobId} failed", scrapeJobId);
            await alerter.HandleFailureAsync(scrapeJobId, ex.Message, ct);
            throw; // Let Hangfire retry
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

            // Parse last completed page from "pass1:page:N/M" format
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
                lastPage = maxPages; // Pass 1 done, skip to pass 2
            }

            // Parse enriched slugs for pass 2 resume (not stored — re-computed from DB)
            var enrichedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return new BackfillProgress(maxPages, lastPage, enrichedSlugs);
        }
        catch
        {
            return new BackfillProgress(200, 0, []);
        }
    }

    private sealed record BackfillProgress(int MaxPages, int LastCompletedPage, HashSet<string> EnrichedSlugs);

    // ── JKAnime directory JSON models ───────────────────────

    private sealed record JkDirectoryPage(
        [property: System.Text.Json.Serialization.JsonPropertyName("current_page")] int CurrentPage,
        [property: System.Text.Json.Serialization.JsonPropertyName("data")] IReadOnlyList<JkDirectoryItem> Data);

    private sealed record JkDirectoryItem(
        [property: System.Text.Json.Serialization.JsonPropertyName("slug")] string? Slug,
        [property: System.Text.Json.Serialization.JsonPropertyName("title")] string? Title,
        [property: System.Text.Json.Serialization.JsonPropertyName("synopsis")] string? Synopsis,
        [property: System.Text.Json.Serialization.JsonPropertyName("image")] string? Image,
        [property: System.Text.Json.Serialization.JsonPropertyName("type")] string? Type,
        [property: System.Text.Json.Serialization.JsonPropertyName("status")] string? Status);

    // ── Series detail enrichment ────────────────────────────

    private async Task<SeriesScrapedData?> ScrapeSeriesDetailAsync(
        string baseUrl, string slug, int delayMs, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/{slug}/";
        if (!await GoToAsync(url, ct))
        {
            logger.LogWarning("Cannot reach series detail {Url}", url);
            return null;
        }

        try
        {
            var title = await Page.Locator("div.anime_info h3").First.InnerTextAsync();

            string? synopsis = null;
            var synopsisEl = Page.Locator("div.anime_info p.scroll");
            if (await synopsisEl.CountAsync() > 0)
                synopsis = (await synopsisEl.First.InnerTextAsync()).Trim();

            var coverUrl = $"https://cdn.jkdesa.com/assets/images/animes/image/{slug}.jpg";

            var genres = new List<string>();
            var genreLinks = await Page.Locator("div.card-bod a[href*='/genero/']").AllAsync();
            foreach (var g in genreLinks)
            {
                var genreName = (await g.InnerTextAsync()).Trim();
                if (!string.IsNullOrWhiteSpace(genreName))
                    genres.Add(genreName);
            }

            string? status = null;
            var statusEl = Page.Locator("div.card-bod div.enemision");
            if (await statusEl.CountAsync() > 0)
            {
                var statusClass = await statusEl.First.GetAttributeAsync("class") ?? "";
                if (statusClass.Contains("currently")) status = "ongoing";
                else if (statusClass.Contains("completed")) status = "completed";
                else
                {
                    var statusText = (await statusEl.First.InnerTextAsync()).Trim().ToLowerInvariant();
                    if (statusText.Contains("emision") || statusText.Contains("emisión")) status = "ongoing";
                    else if (statusText.Contains("finalizado")) status = "completed";
                    else if (statusText.Contains("estrenar")) status = "upcoming";
                }
            }

            string? type = null;
            var tipoEl = Page.Locator("div.card-bod li[rel='tipo']");
            if (await tipoEl.CountAsync() > 0)
            {
                var tipoText = (await tipoEl.First.InnerTextAsync()).Trim().ToLowerInvariant();
                if (tipoText.Contains("serie") || tipoText.Contains("tv")) type = "tv";
                else if (tipoText.Contains("película") || tipoText.Contains("pelicula") || tipoText.Contains("movie")) type = "movie";
                else if (tipoText.Contains("ova")) type = "ova";
                else if (tipoText.Contains("ona")) type = "ona";
                else if (tipoText.Contains("especial") || tipoText.Contains("special")) type = "special";
            }

            // Year — extract from metadata list items (e.g. "Emitido: Oct 2023", "Año: 2023")
            short? year = null;
            var metaItems = await Page.Locator("div.card-bod ul li").AllAsync();
            foreach (var li in metaItems)
            {
                var text = (await li.InnerTextAsync()).Trim();
                var yearMatch = System.Text.RegularExpressions.Regex.Match(text, @"\b(19|20)\d{2}\b");
                if (yearMatch.Success && short.TryParse(yearMatch.Value, out var parsedYear))
                {
                    year = parsedYear;
                    break;
                }
            }

            await JitterDelayAsync(delayMs, ct);

            return new SeriesScrapedData(
                Slug: slug,
                Title: title.Trim(),
                CoverUrl: coverUrl,
                Status: status ?? "ongoing",
                Type: type ?? "tv",
                Synopsis: synopsis,
                Year: year,
                Genres: genres.Count > 0 ? genres : null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse series detail for {Slug}", slug);
            return null;
        }
    }

    // ── Episode discovery ───────────────────────────────────

    private async Task<IReadOnlyList<EpisodeScrapedData>> ScrapeEpisodesFromDetailAsync(
        string baseUrl, string slug, Guid seriesId, int delayMs, CancellationToken ct)
    {
        var result = new List<EpisodeScrapedData>();

        try
        {
            try
            {
                await Page.WaitForSelectorAsync("div.capitulos a[href]",
                    new PageWaitForSelectorOptions { Timeout = 10_000 });
            }
            catch (TimeoutException)
            {
                logger.LogDebug("No episode links for {Slug}", slug);
                return result;
            }

            var rows = await Page.Locator($"div.capitulos a[href*='/{slug}/']").AllAsync();
            foreach (var row in rows)
            {
                try
                {
                    var href = await row.GetAttributeAsync("href");
                    if (href is null) continue;

                    var match = System.Text.RegularExpressions.Regex.Match(href, @"/(\d+)/?$");
                    if (!match.Success || !short.TryParse(match.Groups[1].Value, out var epNum) || epNum <= 0)
                        continue;

                    result.Add(new EpisodeScrapedData(
                        SeriesId: seriesId,
                        EpisodeNumber: epNum,
                        Title: null,
                        PendingMirrors: []));
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to parse episode link");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to extract episode list for {Slug}", slug);
        }

        return result;
    }

    // ── Mirror extraction (extract from JS variables) ──────
    //
    // JKAnime defines `var servers = [...]` with base64-encoded embed URLs
    // for external providers (Streamwish, VOE, Vidhide, Mixdrop, etc.).
    // OK.ru is embedded via `jkokru.php?u=ID` in the `video[]` array.
    // We extract URLs directly from JS evaluation — no tab clicking needed.

    private async Task<IReadOnlyList<MirrorScrapedData>> ScrapeEpisodeMirrorsAsync(
        string episodeUrl, Guid episodeId, int delayMs, CancellationToken ct)
    {
        var mirrors = new List<MirrorScrapedData>();
        var capturedEmbeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!await GoToAsync(episodeUrl, ct))
        {
            logger.LogWarning("Cannot reach episode {Url}", episodeUrl);
            return mirrors;
        }

        // ── 1. Extract external mirrors from the `servers` JS variable ──
        try
        {
            await Page.WaitForFunctionAsync(
                "typeof servers !== 'undefined' && Array.isArray(servers)",
                new PageWaitForFunctionOptions { Timeout = 15_000 });

            var externalUrls = await Page.EvaluateAsync<string[]>(@"
                (() => {
                    const results = [];
                    if (typeof servers !== 'undefined' && Array.isArray(servers)) {
                        for (const s of servers) {
                            if (!s.remote || s.server === 'Mediafire') continue;
                            try {
                                const url = atob(s.remote).trim();
                                if (url.startsWith('http')) results.push(url);
                            } catch {}
                        }
                    }
                    return results;
                })()
            ");

            foreach (var url in externalUrls)
                capturedEmbeds.Add(url);

            logger.LogDebug("Extracted {Count} external mirrors from servers[] on {Url}",
                externalUrls.Length, episodeUrl);
        }
        catch (TimeoutException)
        {
            logger.LogDebug("No servers JS variable found on {Url}", episodeUrl);
        }

        // ── 2. Extract OK.ru mirror from video[] array ──
        try
        {
            var okruId = await Page.EvaluateAsync<string?>(@"
                (() => {
                    if (typeof video === 'undefined' || !Array.isArray(video)) return null;
                    for (const v of video) {
                        const m = v.match(/jkokru\.php\?u=(\d+)/);
                        if (m) return m[1];
                    }
                    return null;
                })()
            ");

            if (!string.IsNullOrWhiteSpace(okruId))
            {
                capturedEmbeds.Add($"https://ok.ru/videoembed/{okruId}");
                logger.LogDebug("Extracted OK.ru mirror: {Id}", okruId);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed extracting OK.ru mirror on {Url}", episodeUrl);
        }

        // ── 3. Build mirror records ──
        short priority = 0;
        foreach (var url in capturedEmbeds)
        {
            if (IsJkAnimeDomain(url)) continue;

            var providerName = ExtractProviderName(url);
            mirrors.Add(new MirrorScrapedData(
                EpisodeId: episodeId,
                ProviderName: providerName,
                EmbedUrl: url,
                QualityLabel: 720,
                Priority: priority++));
        }

        logger.LogDebug("Episode {Url}: captured {Count} real mirrors", episodeUrl, mirrors.Count);

        await JitterDelayAsync(delayMs, ct);
        return mirrors;
    }

    private static bool IsJkAnimeDomain(string url) =>
        url.Contains("jkanime.net", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("jkdesa.com", StringComparison.OrdinalIgnoreCase);

    // ── Helpers ─────────────────────────────────────────────

    private static string MapStatus(string? raw) => raw switch
    {
        "currently" => "ongoing",
        "completed" => "completed",
        "notyet" => "upcoming",
        _ => "ongoing"
    };

    private static string MapType(string? raw) => raw?.ToLowerInvariant() switch
    {
        "tv" => "tv",
        "movie" => "movie",
        "ova" => "ova",
        "ona" => "ona",
        "special" => "special",
        _ => "tv"
    };

    private static string ExtractProviderName(string url)
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
                var h when h.Contains("streamwish") || h.Contains("fastwish") || h.Contains("swish") => "streamwish",
                var h when h.Contains("voe") => "voe",
                var h when h.Contains("desu") || h.Contains("playmudos") => "desu",
                var h when h.Contains("nozomi") => "nozomi",
                var h when h.Contains("mega") => "mega",
                var h when h.Contains("vidhide") => "vidhide",
                _ => host.Split('.')[0]
            };
        }
        catch { return "unknown"; }
    }
}
