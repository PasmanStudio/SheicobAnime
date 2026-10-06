using AnimeIndex.Api.Data;
using AnimeIndex.Api.Infrastructure.Scraping;
using AnimeIndex.Scraper.Infrastructure;
using AnimeIndex.Scraper.Infrastructure.Importers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Strategies;

/// <summary>
/// Cron diario sobre animeav1.com — pure HTTP. Reemplaza a jkanime (Source2) desde
/// oct-2026, cuando su Cloudflare empezó a devolver 403 a todo el tráfico de los runners.
///
/// Los slugs de animeav1 coinciden con los de jkanime (ambos derivan de MAL), así que
/// los episodios nuevos caen sobre las series que ya existen en la DB.
///
///   1. Home → últimos ~20 episodios (descubre series nuevas).
///   2. Series a revisar = las de la home ∪ las ongoing/upcoming de la DB.
///      Por cada una: /media/{slug} → lista de episodios; los que no tienen mirrors
///      activos se completan con /media/{slug}/{n} (embeds SUB).
///   3. Subida a SeekStreaming (resolve → tus), igual que Source2 Phase 3.
///
/// Reutiliza el parser de <see cref="AnimeAv1Importer"/> y el filtrado de embeds de
/// <see cref="SeriesImportService.ClassifyEmbed"/>.
/// </summary>
public sealed class Source3Strategy(
    AppDbContext db,
    UpsertPipelineService upsert,
    AnimeAv1Importer av1,
    MirrorProbeService probe,
    SeekStreamingUploadService? seekStreaming,
    IServiceScopeFactory? scopeFactory,
    IConfiguration config,
    ILogger<Source3Strategy> logger) : IScrapeStrategy
{
    public string SourceKey => "source3";

    public async Task<ScrapeResult> ScrapeAsync(Guid scrapeJobId, CancellationToken ct = default)
    {
        var delayMs = config.GetValue("AnimeAv1:DelayMs", 600);
        // Tope por serie y corrida: una serie larga que aparece por primera vez
        // (ej. un clásico re-subido) se completa de a poco en vez de comerse la corrida.
        var maxEpisodesPerSeries = config.GetValue("AnimeAv1:MaxEpisodesPerSeries", 30);
        // Si las primeras N series fallan todas, la fuente está caída o cambió: cortar.
        const int AbortAfterFailures = 10;

        var seriesCount = 0;
        var episodeCount = 0;
        var mirrorCount = 0;
        var notFoundCount = 0;
        var probeRejected = 0;

        var pendingUploads = new List<(Guid EpisodeId, List<string> Urls)>();

        async Task UpdateHeartbeatAsync(string progress)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    """UPDATE scrape_jobs SET progress_message = {0}, last_heartbeat = now() WHERE id = {1}""",
                    progress, scrapeJobId);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to write heartbeat for job {JobId}", scrapeJobId);
            }
        }

        // ── Phase 1: home → últimos episodios ─────────────────
        var latest = await av1.FetchLatestEpisodesAsync(ct);
        if (latest.Count == 0)
            return new ScrapeResult(false,
                "animeav1: la home no devolvió episodios — el sitio no responde o cambió de estructura.");

        var latestSlugs = latest.Select(l => l.Slug).Distinct().ToList();

        // Series en emisión que ya seguimos (las de jkanime incluidas: mismos slugs).
        var tracked = await db.Series
            .Where(s => (s.Status == "ongoing" || s.Status == "upcoming")
                     && !db.BlockedSlugs.Any(b => b.Slug == s.Slug))
            .OrderBy(s => s.UpdatedAt)
            .Select(s => s.Slug)
            .ToListAsync(ct);

        var slugs = latestSlugs.Concat(tracked).Distinct().ToList();
        var blocked = (await db.BlockedSlugs
                .Where(b => slugs.Contains(b.Slug))
                .Select(b => b.Slug)
                .ToListAsync(ct))
            .ToHashSet();

        logger.LogInformation(
            "animeav1: {Latest} episodio(s) en la home ({New} series) + {Tracked} en emisión → {Total} series a revisar",
            latest.Count, latestSlugs.Count, tracked.Count, slugs.Count);

        // ── Phase 2: series → episodios faltantes → embeds ────
        var attempts = 0;
        foreach (var slug in slugs)
        {
            if (ct.IsCancellationRequested) break;
            if (blocked.Contains(slug))
            {
                logger.LogInformation("animeav1: {Slug} está en blocked_slugs — se omite", slug);
                continue;
            }

            if (seriesCount == 0 && attempts >= AbortAfterFailures)
                return new ScrapeResult(false,
                    $"animeav1: las primeras {attempts} series fallaron — se corta la corrida.");
            attempts++;

            await UpdateHeartbeatAsync(
                $"av1:{seriesCount + notFoundCount}/{slugs.Count} eps:{episodeCount} mirrors:{mirrorCount} notFound:{notFoundCount}");

            var src = await av1.FetchSeriesAsync(slug, ct);
            await Task.Delay(delayMs, ct);
            if (src is null)
            {
                // Serie nuestra (de jkanime) que animeav1 no tiene con ese slug.
                notFoundCount++;
                continue;
            }

            var existing = await db.Series
                .Where(s => s.Slug == slug)
                .Select(s => new { s.Id, s.Title })
                .FirstOrDefaultAsync(ct);

            // Serie nueva, o con el título roto (= slug, lo que dejaba Source2 cuando
            // jkanime devolvía 403): metadata completa de animeav1. Si ya existe, solo
            // se actualiza el estado — portada, sinopsis y géneros quedan como estaban.
            var seriesId = existing is null || existing.Title == slug
                ? await upsert.UpsertSeriesAsync(new SeriesScrapedData(
                    Slug: src.Slug,
                    Title: src.Title,
                    CoverUrl: src.CoverUrl,
                    Status: src.Status,
                    Type: src.Type,
                    Synopsis: src.Synopsis,
                    Year: src.Year,
                    Genres: src.Genres,
                    EpisodeCount: (short?)src.EpisodeNumbers.Count), ct)
                : await upsert.UpsertSeriesAsync(new SeriesScrapedData(
                    Slug: slug,
                    Title: existing.Title,
                    CoverUrl: null,
                    Status: src.Status,
                    Type: null), ct);
            seriesCount++;

            var withMirrors = await db.Episodes
                .Where(e => e.SeriesId == seriesId && e.Mirrors.Any(m => m.IsActive))
                .Select(e => e.EpisodeNumber)
                .ToHashSetAsync(ct);

            var missing = src.EpisodeNumbers
                .Where(n => !withMirrors.Contains(n))
                .OrderByDescending(n => n)
                .Take(maxEpisodesPerSeries)
                .ToList();

            foreach (var number in missing)
            {
                if (ct.IsCancellationRequested) break;

                var episodeId = await upsert.UpsertEpisodeAsync(
                    new EpisodeScrapedData(seriesId, number, Title: null, PendingMirrors: []), ct);
                episodeCount++;

                var embeds = await av1.FetchEpisodeEmbedsAsync(slug, number, ct);
                var uploadUrls = new List<string>();

                foreach (var embed in embeds)
                {
                    if (SeriesImportService.ClassifyEmbed(embed) is not { } c) continue;

                    // Como fuente de subida sirve aunque no sea embebible: el video
                    // termina en nuestro host, la URL de terceros no se guarda.
                    uploadUrls.Add(embed.Url);

                    if (!await probe.IsEmbeddableAsync(embed.Url, ct))
                    {
                        probeRejected++;
                        continue;
                    }

                    await upsert.UpsertMirrorAsync(new MirrorScrapedData(
                        EpisodeId: episodeId,
                        ProviderName: c.Provider,
                        EmbedUrl: embed.Url,
                        QualityLabel: 720,
                        Priority: c.Priority), ct);
                    mirrorCount++;
                }

                if (seekStreaming is not null && uploadUrls.Count > 0)
                    pendingUploads.Add((episodeId, uploadUrls));

                await Task.Delay(delayMs, ct);
            }

            if (missing.Count > 0)
                await upsert.SyncEpisodeCountAsync(seriesId, ct);
        }

        logger.LogInformation(
            "animeav1: series={S} episodios={E} mirrors={M} no-encontradas={N} rechazados-por-probe={P}",
            seriesCount, episodeCount, mirrorCount, notFoundCount, probeRejected);

        // ── Reintentos: episodios recientes con mirrors pero sin subida propia ──
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
                pendingUploads.Add((ep.Id, ep.EmbedUrls));
                retryCount++;
            }

            if (retryCount > 0)
                logger.LogInformation(
                    "SeekStreaming: queued {Count} episode(s) for retry (had mirrors, no seekstreaming mirror, last 7d)",
                    retryCount);
        }

        // ── Phase 3: resolve + upload a SeekStreaming ─────────
        if (pendingUploads.Count > 0 && scopeFactory is not null && !ct.IsCancellationRequested)
            await UploadAsync(pendingUploads, UpdateHeartbeatAsync, ct);

        await UpdateHeartbeatAsync(
            $"av1:done series:{seriesCount} eps:{episodeCount} mirrors:{mirrorCount} notFound:{notFoundCount}");

        if (seriesCount == 0)
            return new ScrapeResult(false,
                "animeav1: no se pudo leer ninguna serie — el sitio no responde o cambió de estructura.");

        return new ScrapeResult(true,
            SeriesIndexed: seriesCount,
            EpisodesIndexed: episodeCount,
            MirrorsIndexed: mirrorCount);
    }

    /// <summary>
    /// Phase A (secuencial): embeds → candidatos MP4 directos. Phase B (paralela):
    /// descarga + tus, cortando en el primer candidato que sube.
    /// </summary>
    private async Task UploadAsync(
        List<(Guid EpisodeId, List<string> Urls)> pending,
        Func<string, Task> heartbeat,
        CancellationToken ct)
    {
        var maxParallel = Math.Clamp(config.GetValue("SeekStreaming:MaxParallelUploads", 20), 1, 20);

        logger.LogInformation("SeekStreaming Phase A: resolving {Count} episode(s)", pending.Count);

        var work = new List<(Guid Id, IReadOnlyList<ResolvedUploadTarget> Candidates)>(pending.Count);
        foreach (var (episodeId, urls) in pending)
        {
            if (ct.IsCancellationRequested) return;
            using var scope = scopeFactory!.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<SeekStreamingUploadService>();
            work.Add((episodeId, await svc.ResolveAllDirectUrlsAsync(episodeId, urls, ct)));
        }

        var withCandidates = work.Where(w => w.Candidates.Count > 0).ToList();
        logger.LogInformation(
            "SeekStreaming Phase B: uploading {Total} episode(s) ({Resolved} with candidates) — maxParallel={Max}",
            work.Count, withCandidates.Count, maxParallel);
        await heartbeat($"av1:uploading {withCandidates.Count}");

        var uploaded = 0;
        var failed = 0;
        using var sem = new SemaphoreSlim(maxParallel, maxParallel);
        var tasks = withCandidates.Select(async w =>
        {
            await sem.WaitAsync(ct);
            try
            {
                using var scope = scopeFactory!.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<SeekStreamingUploadService>();
                foreach (var target in w.Candidates)
                {
                    if (ct.IsCancellationRequested) return;
                    if (await svc.UploadResolvedAsync(target, ct))
                    {
                        Interlocked.Increment(ref uploaded);
                        return;
                    }
                }
                Interlocked.Increment(ref failed);
            }
            finally { sem.Release(); }
        }).ToArray();

        await Task.WhenAll(tasks);

        logger.LogInformation(
            "SeekStreaming Phase B done: {Uploaded} uploaded, {Failed} failed",
            uploaded, failed);
    }
}
