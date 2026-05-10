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
    UpsertPipelineService upsert,
    JkAnimeHttpClient http,
    SeekStreamingUploadService? seekStreaming,
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
        var totalDiscovered = 0;
        var totalNeedingWork = 0;

        // Initialize heartbeat
        async Task UpdateHeartbeatAsync()
        {
            try
            {
                var msg = $"p2:{seriesCount}/{totalNeedingWork} eps:{episodeCount} mirrors:{mirrorCount} noEps:{noEpisodesCount} [discovered:{totalDiscovered}]";
                await db.Database.ExecuteSqlRawAsync(
                    """UPDATE scrape_jobs SET progress_message = {0}, last_heartbeat = now() WHERE id = {1}""",
                    msg, scrapeJobId);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to write heartbeat for job {JobId}", scrapeJobId);
            }
        }

        // ── Phase 1: Discover and upsert all series to DB ─────
        // Returns only the count — in-memory list discarded after upsert.
        totalDiscovered = await DiscoverSeriesAsync(baseUrl, maxPages, delayMs, scrapeJobId, ct);

        // ── Phase 2: Enrich from DB (resumable on container restart) ──
        // Query series that still need episodes or mirrors, 0-episode series first.
        // Because this reads from DB, a container restart re-runs Phase 1 (fast re-upsert)
        // and then picks up Phase 2 exactly where processing left off.
        var slugsNeedingWork = await GetSeriesNeedingEnrichmentAsync(ct);
        totalNeedingWork = slugsNeedingWork.Count;

        logger.LogInformation("Phase 2: {Count} series need enrichment", totalNeedingWork);

        foreach (var (slug, existingStatus) in slugsNeedingWork)
        {
            if (ct.IsCancellationRequested) break;

            // Heartbeat at the start of every series so the stuck-job recovery
            // never fires while a circuit-breaker hold (10 min) is in progress.
            await UpdateHeartbeatAsync();

            if (await http.CheckCircuitBreakerAsync(ct))
                logger.LogWarning("Circuit breaker tripped — paused 10 min");

            // Navigate to series detail page for enrichment + AnimeId
            var detail = await http.GetSeriesDetailAsync(baseUrl, slug, ct);
            var enriched = detail is not null
                ? new SeriesScrapedData(
                    Slug: slug,
                    Title: detail.Title,
                    Synopsis: detail.Synopsis,
                    CoverUrl: detail.CoverUrl,
                    Status: detail.Status ?? existingStatus,
                    Type: detail.Type ?? "tv",
                    TitleRomaji: detail.TitleEnglish,
                    TitleNative: detail.TitleJapanese,
                    Year: detail.Year,
                    Genres: detail.Genres,
                    EpisodeCount: detail.EpisodeCount,
                    Studio: detail.Studio,
                    Season: detail.Season,
                    Demographics: detail.Demographics,
                    Language: detail.Language,
                    DurationMinutes: detail.DurationMinutes,
                    AiredDate: detail.AiredDate,
                    Quality: detail.Quality)
                : new SeriesScrapedData(
                    Slug: slug,
                    Title: slug,
                    CoverUrl: null,
                    Status: existingStatus,
                    Type: "tv");

            var seriesId = await upsert.UpsertSeriesAsync(enriched, ct);
            seriesCount++;

            // ── Discover episodes ─────────────────────────────
            if (detail?.AnimeId is null)
            {
                logger.LogWarning("No anime ID for {Slug} — skipping episode discovery (detail={DetailNull})",
                    slug, detail is null ? "null" : "ok");
                noEpisodesCount++;
                await UpdateHeartbeatAsync();
                await JkAnimeHttpClient.JitterDelayAsync(delayMs, ct);
                continue;
            }

            // Delta fetch: for series already seeded, only retrieve episodes newer
            // than the highest episode number we already have in DB. For One Piece at
            // ep 823 this fetches only the last 1-2 AJAX pages instead of all 17.
            var maxKnownEp = await db.Episodes
                .Where(e => e.SeriesId == seriesId)
                .MaxAsync(e => (short?)e.EpisodeNumber, ct) ?? (short)0;

            var episodes = maxKnownEp > 0
                ? await http.GetNewEpisodesAsync(baseUrl, detail.AnimeId.Value, slug, maxKnownEp, ct)
                : await http.GetAllEpisodesAsync(baseUrl, detail.AnimeId.Value, slug, ct);

            if (maxKnownEp > 0)
                logger.LogDebug("{Slug}: delta-fetch from ep {Max} — {Count} new episode(s)",
                    slug, maxKnownEp, episodes.Count);

            if (episodes.Count == 0)
            {
                if (maxKnownEp == 0)
                {
                    // First-time fetch returned nothing — genuine AJAX failure.
                    logger.LogWarning("Zero episodes returned for {Slug} (animeId={AnimeId}) — AJAX may have failed",
                        slug, detail.AnimeId.Value);
                    noEpisodesCount++;
                }
                else
                {
                    // Delta-fetch found no new episodes since ep {maxKnownEp} — normal.
                    logger.LogDebug("{Slug}: up to date at ep {Max} — no new episodes", slug, maxKnownEp);
                }
            }

            // Pre-load episode numbers that already have active mirrors → skip those
            var episodesWithMirrors = await db.Episodes
                .Where(e => e.SeriesId == seriesId && e.Mirrors.Any(m => m.IsActive))
                .Select(e => e.EpisodeNumber)
                .ToHashSetAsync(ct);

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
                        slug, epNum);
                    continue;
                }

                // Fetch episode page for mirror extraction
                var epUrl = $"{baseUrl.TrimEnd('/')}/{slug}/{epNum}/";
                var mirrorUrls = await http.GetEpisodeMirrorUrlsAsync(epUrl, ct);

                foreach (var url in mirrorUrls)
                {
                    if (IsJkAnimeDomain(url)) continue;

                    var providerName = ExtractProviderName(url);
                    if (BlockedProviders.Contains(providerName)) continue;

                    // Save mirror without blocking probe — embed providers block cloud IPs.
                    // MirrorHealthCheckJob handles async deactivation of dead mirrors.
                    var mirror = new MirrorScrapedData(
                        EpisodeId: episodeId,
                        ProviderName: providerName,
                        EmbedUrl: url,
                        QualityLabel: 720,
                        Priority: GetProviderPriority(providerName));

                    await upsert.UpsertMirrorAsync(mirror, ct);
                    mirrorCount++;
                }

                // Upload to SeekStreaming (own mirror, priority=0) — non-blocking on failure.
                if (mirrorUrls.Count > 0 && seekStreaming is not null)
                {
                    var target = await seekStreaming.ResolveDirectUrlAsync(episodeId, mirrorUrls, ct);
                    if (target is not null)
                        await seekStreaming.UploadResolvedAsync(target, ct);
                }

                // Heartbeat after every episode to prevent stuck-job detection
                // killing active jobs that have many episodes.
                await UpdateHeartbeatAsync();
                await JkAnimeHttpClient.JitterDelayAsync(delayMs, ct);
            }

            // Sync episode count from actual DB records
            await upsert.SyncEpisodeCountAsync(seriesId, ct);
        }

        logger.LogInformation(
            "JKAnimeStrategy complete — series={S} episodes={E} mirrors={M} noEps={N}",
            seriesCount, episodeCount, mirrorCount, noEpisodesCount);

        // Final heartbeat
        await UpdateHeartbeatAsync();

        if (seriesCount == 0 && episodeCount == 0)
            return new ScrapeResult(false,
                "Zero series and episodes indexed — JKAnime may be unreachable or has changed structure.");

        return new ScrapeResult(true,
            SeriesIndexed: seriesCount,
            EpisodesIndexed: episodeCount,
            MirrorsIndexed: mirrorCount);
    }

    // ── Directory browsing ──────────────────────────────────

    /// <summary>
    /// Browses /directorio pages and upserts each series immediately.
    /// Returns the total count of discovered series — does NOT keep them in memory.
    /// </summary>
    private async Task<int> DiscoverSeriesAsync(
        string baseUrl, int maxPages, int delayMs, Guid scrapeJobId, CancellationToken ct)
    {
        var discovered = 0;

        for (var page = 1; page <= maxPages; page++)
        {
            if (ct.IsCancellationRequested) break;
            if (await http.CheckCircuitBreakerAsync(ct)) break;

            // Write heartbeat every 10 pages during discovery
            if (page % 10 == 0)
            {
                try
                {
                    var msg = $"p1:discovering page {page}/{maxPages} ({discovered} series so far)";
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

                // Upsert immediately so series are in DB for Phase 2 to pick up.
                var isBlocked = await db.BlockedSlugs.AnyAsync(b => b.Slug == item.Slug.Trim(), ct);
                if (!isBlocked)
                {
                    await upsert.UpsertSeriesAsync(new SeriesScrapedData(
                        Slug: item.Slug.Trim(),
                        Title: item.Title.Trim(),
                        CoverUrl: item.Image,
                        Status: status,
                        Type: type,
                        Synopsis: string.IsNullOrWhiteSpace(item.Synopsis) ? null : item.Synopsis.Trim()), ct);
                    discovered++;
                }
            }

            logger.LogDebug("Page {Page}/{LastPage}: discovered {Count} series",
                page, directoryPage.LastPage, directoryPage.Data.Count);

            // Stop when JKAnime says there are no more pages.
            if (page >= directoryPage.LastPage) break;

            await JkAnimeHttpClient.JitterDelayAsync(delayMs, ct);
        }

        return discovered;
    }

    // ── DB query for Phase 2 ────────────────────────────────

    private record SlugStatus(string Slug, string? Status);

    /// <summary>
    /// Returns slugs that need enrichment: only series with status = 'ongoing'.
    /// Initial full-scrape is complete; daily runs focus on airing series only
    /// (~84 series vs the previous 223) to keep each cycle under ~15 minutes.
    /// Blocked slugs are excluded. Ordered by updated_at ASC so least-recently
    /// scraped series are processed first.
    /// </summary>
    private async Task<IReadOnlyList<(string Slug, string? Status)>> GetSeriesNeedingEnrichmentAsync(
        CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<SlugStatus>(
            $"""
            SELECT s.slug AS "Slug", s.status AS "Status"
            FROM series s
            WHERE NOT EXISTS (SELECT 1 FROM blocked_slugs b WHERE b.slug = s.slug)
            AND s.status = 'ongoing'
            ORDER BY s.updated_at ASC
            """)
            .ToListAsync(ct);

        return rows.Select(r => (r.Slug, r.Status)).ToList();
    }

    // ── Provider helpers ────────────────────────────────────

    private static bool IsJkAnimeDomain(string url) =>
        url.Contains("jkanime.net", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("jkdesa.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("jkplayers.com", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> BlockedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "mega", "mediafire", "desu",
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
