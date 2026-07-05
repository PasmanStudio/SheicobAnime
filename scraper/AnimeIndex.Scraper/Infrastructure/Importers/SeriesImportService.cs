using AnimeIndex.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.Importers;

/// <summary>Outcome of a single-series import.</summary>
public sealed record ImportSummary(
    bool Success,
    string? Message = null,
    string? Slug = null,
    string? Title = null,
    int Episodes = 0,
    int Mirrors = 0,
    int Uploaded = 0);

/// <summary>
/// Source-agnostic orchestrator for the per-series import flow. Resolves the
/// <see cref="ISeriesImporter"/> by source key, pulls metadata + episodes +
/// embeds, and writes everything through <see cref="UpsertPipelineService"/>
/// — exactly the same DB contract the daily scraper uses.
///
/// When upload is enabled and a SeekStreaming API key is present, it reuses the
/// existing two-phase upload pipeline (resolve → tus upload) to mirror episodes
/// onto our own host, then purges the frontend cache. The upload service is
/// resolved lazily per scope: if it isn't registered (no key / --no-upload), the
/// import still succeeds with just the source embeds.
/// </summary>
public sealed class SeriesImportService(
    IEnumerable<ISeriesImporter> importers,
    AppDbContext db,
    UpsertPipelineService upsert,
    SiteRevalidationService revalidation,
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<SeriesImportService> logger)
{
    // Embeds we never store as a playable mirror: animeav1's own player/HLS, and
    // pure file-locker hosts that aren't usable embeds for the site. Matched by the
    // source's own server label (canonical key), with URL markers as a safety net.
    private static readonly HashSet<string> SkipProviders =
        new(StringComparer.OrdinalIgnoreCase)
        { "hls", "upnshare", "mega", "terabox", "1fichier", "desu" };

    private static readonly string[] SkipUrlMarkers =
        ["zilla-networks", "uns.bio", "animeav1", ".m3u8"];

    // Mirror priority for the providers animeav1 exposes (lower = preferred).
    private static readonly Dictionary<string, short> ProviderPriorities =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["okru"] = 1, ["mp4upload"] = 2, ["yourupload"] = 4, ["streamwish"] = 6,
            ["voe"] = 7, ["vidhide"] = 8, ["streamtape"] = 9,
        };

    public async Task<ImportSummary> ImportAsync(
        string sourceKey, string? query, string? explicitSlug, bool upload, CancellationToken ct)
    {
        var importer = importers.FirstOrDefault(i =>
            string.Equals(i.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase));
        if (importer is null)
            return new ImportSummary(false, $"No hay importer para la fuente '{sourceKey}'.");

        // ── Resolve the slug (explicit wins; otherwise search) ──
        string slug;
        if (!string.IsNullOrWhiteSpace(explicitSlug))
        {
            slug = explicitSlug.Trim();
        }
        else
        {
            if (string.IsNullOrWhiteSpace(query))
                return new ImportSummary(false, "Falta --query (o --slug).");

            var results = await importer.SearchAsync(query, ct);
            if (results.Count == 0)
                return new ImportSummary(false, $"Sin resultados en {sourceKey} para \"{query}\".");

            slug = PickBest(query, results);
            logger.LogInformation("Búsqueda \"{Query}\" → {Count} candidato(s): {Slugs} → elijo {Pick}",
                query, results.Count, string.Join(", ", results.Take(5).Select(r => r.Slug)), slug);
        }

        // ── Gate: blocked slugs (same guard as the daily scraper) ──
        if (await db.BlockedSlugs.AnyAsync(b => b.Slug == slug, ct))
            return new ImportSummary(false, $"El slug '{slug}' está en blocked_slugs.");

        // ── Series metadata ──
        var series = await importer.FetchSeriesAsync(slug, ct);
        if (series is null)
            return new ImportSummary(false, $"No se pudo leer la serie '{slug}' en {sourceKey}.");

        var seriesId = await upsert.UpsertSeriesAsync(new SeriesScrapedData(
            Slug: series.Slug,
            Title: series.Title,
            CoverUrl: series.CoverUrl,
            Status: series.Status,
            Type: series.Type,
            Synopsis: series.Synopsis,
            Year: series.Year,
            Genres: series.Genres,
            EpisodeCount: (short?)series.EpisodeNumbers.Count), ct);

        // ── Episodes + mirrors ──
        var pendingUploads = new List<(Guid EpisodeId, List<string> Urls)>();
        var episodeCount = 0;
        var mirrorCount = 0;

        foreach (var number in series.EpisodeNumbers)
        {
            if (ct.IsCancellationRequested) break;

            var episodeId = await upsert.UpsertEpisodeAsync(
                new EpisodeScrapedData(seriesId, number, Title: null, PendingMirrors: []), ct);
            episodeCount++;

            var embeds = await importer.FetchEpisodeEmbedsAsync(slug, number, ct);
            var uploadUrls = new List<string>();

            foreach (var embed in embeds)
            {
                // Prefer the source's own server label (e.g. "VidHide") over parsing the
                // URL host: animeav1 serves some hosts via rotating CDN domains (ryderjet.com)
                // that a host-based parser would misname.
                var provider = MapServer(embed.Server);
                if (SkipProviders.Contains(provider) || ShouldSkip(embed.Url)) continue;

                await upsert.UpsertMirrorAsync(new MirrorScrapedData(
                    EpisodeId: episodeId,
                    ProviderName: provider,
                    EmbedUrl: embed.Url,
                    QualityLabel: 720,
                    Priority: ProviderPriorities.GetValueOrDefault(provider, (short)50)), ct);
                mirrorCount++;
                uploadUrls.Add(embed.Url);
            }

            if (upload && uploadUrls.Count > 0)
                pendingUploads.Add((episodeId, uploadUrls));

            // Be polite to the source between episode pages.
            await Task.Delay(config.GetValue("AnimeAv1:DelayMs", 600), ct);
        }

        await upsert.SyncEpisodeCountAsync(seriesId, ct);

        // ── Upload to our own host (best-effort, parallel) ──
        var uploaded = upload
            ? await UploadEpisodesAsync(pendingUploads, ct)
            : 0;

        // ── Purge frontend cache so the new series shows up immediately ──
        await revalidation.RevalidateAsync("content", ct);

        logger.LogInformation(
            "Import OK — slug={Slug} eps={Eps} mirrors={Mirrors} subidos={Uploaded}",
            slug, episodeCount, mirrorCount, uploaded);

        return new ImportSummary(true, "OK", slug, series.Title, episodeCount, mirrorCount, uploaded);
    }

    /// <summary>
    /// Reuses the SeekStreaming two-phase pipeline (resolve → upload) just like
    /// <see cref="Source2Strategy"/> Phase 3. Resolved lazily: returns 0 if the
    /// upload service isn't registered (no API key configured).
    /// </summary>
    private async Task<int> UploadEpisodesAsync(
        List<(Guid EpisodeId, List<string> Urls)> pending, CancellationToken ct)
    {
        if (pending.Count == 0) return 0;

        // Probe registration once: if there's no SeekStreaming key, the chain isn't
        // registered and GetService returns null — skip uploads entirely.
        using (var probe = scopeFactory.CreateScope())
        {
            if (probe.ServiceProvider.GetService<SeekStreamingUploadService>() is null)
            {
                logger.LogInformation("Upload omitido: SeekStreaming no configurado (faltan embeds propios).");
                return 0;
            }
        }

        var maxParallel = Math.Clamp(config.GetValue("SeekStreaming:MaxParallelUploads", 20), 1, 20);

        // Phase A — resolve embed URLs → direct MP4 candidates (sequential, short HTTP calls).
        var work = new List<(Guid Id, IReadOnlyList<ResolvedUploadTarget> Candidates)>(pending.Count);
        foreach (var (episodeId, urls) in pending)
        {
            if (ct.IsCancellationRequested) break;
            using var scope = scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<SeekStreamingUploadService>();
            var candidates = await svc.ResolveAllDirectUrlsAsync(episodeId, urls, ct);
            work.Add((episodeId, candidates));
        }

        // Phase B — download + tus upload in parallel; stop at first success per episode.
        var uploaded = 0;
        using var sem = new SemaphoreSlim(maxParallel, maxParallel);
        var tasks = work.Where(w => w.Candidates.Count > 0).Select(async w =>
        {
            await sem.WaitAsync(ct);
            try
            {
                using var scope = scopeFactory.CreateScope();
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
            }
            finally { sem.Release(); }
        }).ToArray();

        await Task.WhenAll(tasks);
        return uploaded;
    }

    private static bool ShouldSkip(string url) =>
        Array.Exists(SkipUrlMarkers, marker => url.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Maps the source's server label to a canonical provider key. Falls back to a
    /// slugified label, so an unknown host still gets a stable, sane name.
    /// </summary>
    private static string MapServer(string server) => server.Trim().ToLowerInvariant() switch
    {
        "mp4upload" => "mp4upload",
        "yourupload" => "yourupload",
        "streamtape" => "streamtape",
        "vidhide" => "vidhide",
        "streamwish" => "streamwish",
        "voe" => "voe",
        "okru" or "ok.ru" => "okru",
        "filemoon" => "filemoon",
        "mega" => "mega",
        "terabox" => "terabox",
        "1fichier" => "1fichier",
        "hls" => "hls",
        "upnshare" => "upnshare",
        var s => new string(s.Where(c => char.IsLetterOrDigit(c)).ToArray()),
    };

    /// <summary>
    /// Picks the best candidate for a free-text query: most query tokens present in the
    /// slug, then the shortest (most specific base) slug, then original relevance order.
    /// E.g. "frieren" → "sousou-no-frieren" over "sousou-no-frieren-2nd-season".
    ///
    /// Tokeniza partiendo por CUALQUIER caracter no alfanumérico (no solo espacio/guion):
    /// antes "Mushoku Tensei III: …" dejaba el token "iii:" con dos puntos, que nunca
    /// matcheaba el "iii" del slug → empataba con la temporada 1 y el desempate por
    /// slug más corto elegía la 1 en vez de la 3. Público para test.
    /// </summary>
    public static string PickBest(string query, IReadOnlyList<SourceSeriesRef> results)
    {
        var tokens = Tokenize(query);

        return results
            .Select((r, idx) => (r.Slug, idx, norm: r.Slug.Replace('-', ' ').ToLowerInvariant()))
            .OrderByDescending(x => tokens.Count(t => x.norm.Contains(t)))
            .ThenBy(x => x.Slug.Length)
            .ThenBy(x => x.idx)
            .First().Slug;
    }

    private static string[] Tokenize(string text) =>
        System.Text.RegularExpressions.Regex
            .Split(text.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(t => t.Length > 0)
            .ToArray();
}
