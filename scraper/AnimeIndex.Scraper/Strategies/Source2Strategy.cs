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
    KatanimeHttpClient? katanime,
    IServiceScopeFactory? scopeFactory,
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
        // Directorio listing es liviano (JSON embebido) y NO escribe DB por página
        // tras el dedup en memoria — un delay menor que el de páginas de episodio.
        var discoveryDelayMs = config.GetValue("Source2:DiscoveryDelayMs", 700);

        var seriesCount = 0;
        var episodeCount = 0;
        var mirrorCount = 0;
        var noEpisodesCount = 0;

        // Accumulate upload targets during Phase 2; uploaded in parallel at the end.
        // Slug/episode/title travel with each item so the katanime fallback can locate
        // the exact episode when jkanime yields nothing or every jkanime upload fails.
        var pendingUploads = new List<(Guid EpisodeId, List<string> MirrorUrls, string Slug, short EpisodeNumber, string? Title)>();

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
        totalDiscovered = await DiscoverSeriesAsync(baseUrl, maxPages, discoveryDelayMs, scrapeJobId, ct);

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

                // Queue for SeekStreaming upload — resolved and uploaded in parallel after Phase 2.
                // Queue even when jkanime returned zero mirrors so the katanime fallback can still
                // try this specific episode (covers the "jkanime blocked / had nothing" case).
                if (seekStreaming is not null)
                    pendingUploads.Add((episodeId, mirrorUrls.ToList(), slug, epNum, ep.Title));

                // Heartbeat after every episode to prevent stuck-job detection
                // killing active jobs that have many episodes.
                await UpdateHeartbeatAsync();
                await JkAnimeHttpClient.JitterDelayAsync(delayMs, ct);
            }

            // Sync episode count from actual DB records
            await upsert.SyncEpisodeCountAsync(seriesId, ct);
        }

        // ── Enqueue retry uploads: episodes with mirrors but no seekstreaming mirror ──
        // Covers episodes whose previous upload failed (e.g. 409 Conflict) — they were
        // skipped by the delta-fetch above since they already exist in DB with mirrors.
        if (seekStreaming is not null && !ct.IsCancellationRequested)
        {
            var retryWindow = DateTime.UtcNow.AddDays(-7);
            var alreadyQueued = pendingUploads.Select(p => p.EpisodeId).ToHashSet();

            var retryEpisodes = await db.Episodes
                .Where(e => e.CreatedAt >= retryWindow
                         && e.Mirrors.Any(m => m.IsActive)
                         && !e.Mirrors.Any(m => m.IsActive && m.ProviderName == "seekstreaming"))
                .Select(e => new
                {
                    e.Id,
                    e.Series.Slug,
                    e.EpisodeNumber,
                    e.Title,
                    EmbedUrls = e.Mirrors
                        .Where(m => m.IsActive)
                        .OrderBy(m => m.Priority)
                        .Select(m => m.EmbedUrl)
                        .ToList()
                })
                .ToListAsync(ct);

            var retryCount = 0;
            foreach (var ep in retryEpisodes)
            {
                if (alreadyQueued.Contains(ep.Id) || ep.EmbedUrls.Count == 0) continue;
                pendingUploads.Add((ep.Id, ep.EmbedUrls, ep.Slug, ep.EpisodeNumber, ep.Title));
                retryCount++;
            }

            if (retryCount > 0)
                logger.LogInformation(
                    "SeekStreaming: queued {Count} episode(s) for retry (had mirrors, no seekstreaming mirror, last 7d)",
                    retryCount);
        }

        // ── Phase 3: resolve + upload to SeekStreaming in parallel ────────────
        if (pendingUploads.Count > 0 && scopeFactory is not null && !ct.IsCancellationRequested)
        {
            var maxParallel = Math.Clamp(
                config.GetValue("SeekStreaming:MaxParallelUploads", 20), 1, 20);

            logger.LogInformation(
                "SeekStreaming Phase A: resolving {Count} episode(s)",
                pendingUploads.Count);

            // Phase A: resolve jkanime embed URLs → ALL direct MP4 candidates per episode
            // (sequential; each call is a short HTTP round-trip, ~1-2 s per episode).
            // We keep the FULL ordered candidate list (not just the first) so Phase B can
            // fall back to the next provider when an upload fails — e.g. mp4upload returns
            // 403 from cloud IPs, or a file's transcoding times out, so we retry okru/voe.
            // Episodes with zero jkanime candidates are kept too: Phase B sends them straight
            // to the katanime fallback.
            var workItems = new List<UploadWorkItem>(pendingUploads.Count);
            foreach (var (episodeId, mirrorUrls, slug, epNum, title) in pendingUploads)
            {
                if (ct.IsCancellationRequested) break;
                IReadOnlyList<ResolvedUploadTarget> all = [];
                if (mirrorUrls.Count > 0)
                {
                    using var scope = scopeFactory.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<SeekStreamingUploadService>();
                    all = await svc.ResolveAllDirectUrlsAsync(episodeId, mirrorUrls, ct);
                }
                workItems.Add(new UploadWorkItem(episodeId, slug, epNum, title, all));
            }

            var withCandidates = workItems.Count(w => w.JkCandidates.Count > 0);
            logger.LogInformation(
                "SeekStreaming Phase B: uploading {Total} episode(s) ({Resolved} with jkanime candidates, rest rely on katanime) — maxParallel={Max}",
                workItems.Count, withCandidates, maxParallel);

            // Phase B: upload each episode in parallel. Per episode we try the jkanime-resolved
            // providers in priority order, STOP at the first success, and on total failure (or no
            // jkanime candidates at all) fall back to katanime for that exact episode.
            var uploaded    = 0;
            var uploadFailed = 0;
            using var sem = new SemaphoreSlim(maxParallel, maxParallel);

            var uploadTasks = workItems.Select(async item =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    if (ct.IsCancellationRequested) return;
                    var ok = await TryUploadEpisodeAsync(item, ct);
                    if (ok) Interlocked.Increment(ref uploaded);
                    else    Interlocked.Increment(ref uploadFailed);
                }
                finally { sem.Release(); }
            }).ToArray();

            await Task.WhenAll(uploadTasks);

            logger.LogInformation(
                "SeekStreaming Phase B done: {Uploaded} uploaded, {Failed} failed",
                uploaded, uploadFailed);
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

    // ── Upload (jkanime candidates → katanime fallback) ─────

    /// <summary>One episode's upload work: its resolved jkanime candidates plus the
    /// identity needed to locate the same episode on katanime if jkanime fails.</summary>
    private sealed record UploadWorkItem(
        Guid EpisodeId,
        string Slug,
        short EpisodeNumber,
        string? Title,
        IReadOnlyList<ResolvedUploadTarget> JkCandidates);

    /// <summary>
    /// Uploads one episode to SeekStreaming. Tries the jkanime-resolved candidates first
    /// (priority order, stop at first success); if there are none or all of them fail, falls
    /// back to katanime for that specific episode — fetch its players, persist them as mirrors,
    /// then try uploading those in priority order. Returns true if any upload succeeded.
    /// </summary>
    private async Task<bool> TryUploadEpisodeAsync(UploadWorkItem item, CancellationToken ct)
    {
        if (scopeFactory is null) return false;

        using var scope = scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<SeekStreamingUploadService>();

        // 1. jkanime-resolved candidates, in priority order.
        for (var i = 0; i < item.JkCandidates.Count; i++)
        {
            if (ct.IsCancellationRequested) return false;
            var target = item.JkCandidates[i];
            if (await svc.UploadResolvedAsync(target, ct)) return true;

            var next = i + 1 < item.JkCandidates.Count ? item.JkCandidates[i + 1].Provider : "katanime";
            logger.LogInformation(
                "Episode {Id}: jkanime upload via {Provider} failed — falling back to {Next}",
                target.EpisodeId, target.Provider, next);
        }

        // 2. katanime fallback for this exact episode.
        if (katanime is null || ct.IsCancellationRequested) return false;

        var kataUrls = await katanime.GetEpisodeMirrorUrlsAsync(item.Slug, item.EpisodeNumber, item.Title, ct);
        if (kataUrls.Count == 0) return false;

        // Persist katanime mirrors so the site has embed fallbacks even if the upload also fails.
        await PersistKatanimeMirrorsAsync(scope, item.EpisodeId, kataUrls, ct);

        var kataCandidates = await svc.ResolveAllDirectUrlsAsync(item.EpisodeId, kataUrls, ct);
        for (var i = 0; i < kataCandidates.Count; i++)
        {
            if (ct.IsCancellationRequested) return false;
            var target = kataCandidates[i];
            if (await svc.UploadResolvedAsync(target, ct))
            {
                logger.LogInformation(
                    "Episode {Id}: uploaded via katanime fallback ({Provider})",
                    item.EpisodeId, target.Provider);
                return true;
            }
        }

        // Único log de fallo real por episodio: se agotaron jkanime + katanime.
        // Los embeds (mixdrop, etc.) quedan igual como mirrors para el usuario;
        // el sistema de reintentos de 7 días lo vuelve a intentar mañana.
        logger.LogInformation(
            "Episode {Id} ({Slug} EP {Ep}): sin subida a Sheicob este run — quedan embeds de terceros, reintenta en 7d",
            item.EpisodeId, item.Slug, item.EpisodeNumber);
        return false;
    }

    /// <summary>Upserts katanime-resolved provider embeds as mirrors (same filtering + priority as jkanime).</summary>
    private static async Task PersistKatanimeMirrorsAsync(
        IServiceScope scope, Guid episodeId, IReadOnlyList<string> urls, CancellationToken ct)
    {
        var upsertSvc = scope.ServiceProvider.GetRequiredService<UpsertPipelineService>();
        foreach (var url in urls)
        {
            if (IsJkAnimeDomain(url)) continue;
            var provider = ExtractProviderName(url);
            if (BlockedProviders.Contains(provider)) continue;

            await upsertSvc.UpsertMirrorAsync(new MirrorScrapedData(
                EpisodeId: episodeId,
                ProviderName: provider,
                EmbedUrl: url,
                QualityLabel: 720,
                Priority: GetProviderPriority(provider)), ct);
        }
    }

    // ── Directory browsing ──────────────────────────────────

    /// <summary>
    /// Browses /directorio pages and upserts each series immediately.
    /// Returns the total count of discovered series — does NOT keep them in memory.
    /// </summary>
    private async Task<int> DiscoverSeriesAsync(
        string baseUrl, int maxPages, int delayMs, Guid scrapeJobId, CancellationToken ct)
    {
        // Pre-cargar slugs bloqueados + existentes UNA vez. Antes esto era un N+1
        // brutal: por CADA serie del catálogo se hacía un AnyAsync (blocked) + un
        // UpsertSeriesAsync (2 round-trips), re-escribiendo TODO el catálogo cada
        // corrida → miles de viajes secuenciales a Supabase (~20 min de la corrida).
        // Ahora discovery solo INSERTA series nuevas; las existentes las refresca
        // la Fase 2 (enrichment de 'ongoing'), y las 'completed' son inmutables
        // (UpsertSeriesAsync nunca las saca de 'completed').
        var blockedSlugs = (await db.BlockedSlugs.Select(b => b.Slug).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownSlugs = (await db.Series.Select(s => s.Slug).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var discovered = 0;
        var inserted = 0;

        for (var page = 1; page <= maxPages; page++)
        {
            if (ct.IsCancellationRequested) break;
            if (await http.CheckCircuitBreakerAsync(ct)) break;

            // Write heartbeat every 10 pages during discovery
            if (page % 10 == 0)
            {
                try
                {
                    var msg = $"p1:discovering page {page}/{maxPages} ({discovered} seen, {inserted} new)";
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

                var slug = item.Slug.Trim();
                discovered++;

                // Bloqueada o ya conocida → sin escritura. knownSlugs.Add devuelve
                // false si ya estaba (también dedup de páginas repetidas).
                if (blockedSlugs.Contains(slug) || !knownSlugs.Add(slug))
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

                // Insert inmediato para que la serie esté en DB y la Fase 2 la tome.
                await upsert.UpsertSeriesAsync(new SeriesScrapedData(
                    Slug: slug,
                    Title: item.Title.Trim(),
                    CoverUrl: item.Image,
                    Status: status,
                    Type: type,
                    Synopsis: string.IsNullOrWhiteSpace(item.Synopsis) ? null : item.Synopsis.Trim()), ct);
                inserted++;
            }

            logger.LogDebug("Page {Page}/{LastPage}: {Count} series ({Inserted} new so far)",
                page, directoryPage.LastPage, directoryPage.Data.Count, inserted);

            // Stop when JKAnime says there are no more pages.
            if (page >= directoryPage.LastPage) break;

            await JkAnimeHttpClient.JitterDelayAsync(delayMs, ct);
        }

        if (inserted > 0)
            logger.LogInformation(
                "Discovery: {Inserted} serie(s) nueva(s) insertada(s) ({Discovered} vistas en el catálogo)",
                inserted, discovered);

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
        "mega", "desu",
    };

    private static readonly Dictionary<string, short> ProviderPriorities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["okru"] = 1, ["mp4upload"] = 2, ["sendvid"] = 3, ["yourupload"] = 4,
        ["filemoon"] = 5, ["streamwish"] = 6, ["voe"] = 7, ["vidhide"] = 8,
        ["streamtape"] = 9, ["fembed"] = 10, ["doodstream"] = 11, ["nozomi"] = 12,
        ["mixdrop"] = 13, ["mediafire"] = 14, ["desu"] = 15,
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
                var h when h.Contains("mediafire") => "mediafire",
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
