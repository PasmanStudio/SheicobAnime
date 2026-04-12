using AnimeIndex.Api.Data;
using AnimeIndex.Api.Infrastructure.Scraping;
using AnimeIndex.Scraper.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace AnimeIndex.Scraper.Strategies;

/// <summary>
/// JKAnime scraper (jkanime.net).
/// Browse /directorio with pagination → series detail → episode pages → Desu player iframe.
/// </summary>
public sealed class Source2Strategy(
    AppDbContext db,
    MirrorProbeService probe,
    UpsertPipelineService upsert,
    IConfiguration config,
    ILogger<Source2Strategy> logger) : PlaywrightBase, IScrapeStrategy
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

        await using var _ = this;
        await InitializeAsync();

        logger.LogInformation("JKAnimeStrategy starting — baseUrl={BaseUrl}, maxPages={MaxPages}", baseUrl, maxPages);

        // ── Discover series from /directorio ──────────────────
        var discovered = await DiscoverSeriesAsync(baseUrl, maxPages, delayMs, ct);

        foreach (var series in discovered)
        {
            if (ct.IsCancellationRequested) break;

            if (await CheckCircuitBreakerAsync(ct))
                logger.LogWarning("Circuit breaker tripped — paused 10 min");

            var blocked = await db.BlockedSlugs.AnyAsync(b => b.Slug == series.Slug, ct);
            if (blocked)
            {
                logger.LogDebug("Skipping blocked slug {Slug}", series.Slug);
                continue;
            }

            // Navigate to series detail page for enrichment
            var detail = await ScrapeSeriesDetailAsync(baseUrl, series.Slug, delayMs, ct);
            var enriched = detail ?? series;

            var seriesId = await upsert.UpsertSeriesAsync(enriched, ct);
            seriesCount++;

            // ── Discover episodes from detail page ────────────
            var episodes = await ScrapeEpisodesFromDetailAsync(baseUrl, series.Slug, seriesId, delayMs, ct);

            foreach (var ep in episodes)
            {
                if (ct.IsCancellationRequested) break;

                var episodeId = await upsert.UpsertEpisodeAsync(ep, ct);
                episodeCount++;

                // Navigate to episode page to extract Desu player mirrors
                var epUrl = $"{baseUrl.TrimEnd('/')}/{series.Slug}/{ep.EpisodeNumber}/";
                var mirrors = await ScrapeEpisodeMirrorsAsync(epUrl, episodeId, delayMs, ct);

                foreach (var mirror in mirrors)
                {
                    var embeddable = await probe.IsEmbeddableAsync(mirror.EmbedUrl, ct);
                    if (!embeddable)
                    {
                        logger.LogDebug("Mirror {Url} not embeddable — skipped", mirror.EmbedUrl);
                        continue;
                    }

                    await upsert.UpsertMirrorAsync(mirror, ct);
                    mirrorCount++;
                }

                await JitterDelayAsync(delayMs, ct);
            }

            // Sync episode count from actual DB records
            await upsert.SyncEpisodeCountAsync(seriesId, ct);
        }

        logger.LogInformation(
            "JKAnimeStrategy complete — series={S} episodes={E} mirrors={M}",
            seriesCount, episodeCount, mirrorCount);

        return new ScrapeResult(true,
            SeriesIndexed: seriesCount,
            EpisodesIndexed: episodeCount,
            MirrorsIndexed: mirrorCount);
    }

    // ── Directory browsing (/directorio?p=N) ────────────────

    private async Task<IReadOnlyList<SeriesScrapedData>> DiscoverSeriesAsync(
        string baseUrl, int maxPages, int delayMs, CancellationToken ct)
    {
        var result = new List<SeriesScrapedData>();

        for (var page = 1; page <= maxPages; page++)
        {
            if (ct.IsCancellationRequested) break;
            if (await CheckCircuitBreakerAsync(ct)) break;

            var url = $"{baseUrl.TrimEnd('/')}/directorio?p={page}";
            if (!await GoToAsync(url, ct))
            {
                logger.LogWarning("Cannot reach {Url}", url);
                break;
            }

            // JKAnime embeds directory data as `var animes = {...}` JSON in an inline script.
            // Extract it via JS evaluation instead of fragile CSS selectors.
            string? animesJson = null;
            try
            {
                await Page.WaitForFunctionAsync("typeof animes !== 'undefined' && animes.data && animes.data.length > 0",
                    new PageWaitForFunctionOptions { Timeout = 10_000 });
                animesJson = await Page.EvaluateAsync<string>("JSON.stringify(animes)");
            }
            catch (TimeoutException)
            {
                logger.LogDebug("No animes variable found on page {Page} — end of directory", page);
                break;
            }

            if (string.IsNullOrWhiteSpace(animesJson))
            {
                logger.LogDebug("Empty animes data on page {Page} — end of directory", page);
                break;
            }

            var animesPage = System.Text.Json.JsonSerializer.Deserialize<JkDirectoryPage>(animesJson);
            if (animesPage?.Data is null || animesPage.Data.Count == 0)
            {
                logger.LogDebug("No series data on page {Page} — end of directory", page);
                break;
            }

            foreach (var item in animesPage.Data)
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

                result.Add(new SeriesScrapedData(
                    Slug: item.Slug.Trim(),
                    Title: item.Title.Trim(),
                    CoverUrl: item.Image,
                    Status: status,
                    Type: type,
                    Synopsis: string.IsNullOrWhiteSpace(item.Synopsis) ? null : item.Synopsis.Trim()));
            }

            logger.LogDebug("Page {Page}: discovered {Count} series", page, animesPage.Data.Count);
            await JitterDelayAsync(delayMs, ct);
        }

        return result;
    }

    // JSON model for `var animes` on /directorio pages
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

    // ── Series detail page (/{slug}/) ───────────────────────

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
            // Title — div.anime_info h3
            var title = await Page.Locator("div.anime_info h3").First.InnerTextAsync();

            // Synopsis — p.scroll inside anime_info
            string? synopsis = null;
            var synopsisEl = Page.Locator("div.anime_info p.scroll");
            if (await synopsisEl.CountAsync() > 0)
                synopsis = (await synopsisEl.First.InnerTextAsync()).Trim();

            // Cover from CDN pattern
            var coverUrl = $"https://cdn.jkdesa.com/assets/images/animes/image/{slug}.jpg";

            // Genres from genre links inside card-bod
            var genres = new List<string>();
            var genreLinks = await Page.Locator("div.card-bod a[href*='/genero/']").AllAsync();
            foreach (var g in genreLinks)
            {
                var genreName = (await g.InnerTextAsync()).Trim();
                if (!string.IsNullOrWhiteSpace(genreName))
                    genres.Add(genreName);
            }

            // Status from div.enemision class (currently | completed | notyet)
            string? status = null;
            var statusEl = Page.Locator("div.card-bod div.enemision");
            if (await statusEl.CountAsync() > 0)
            {
                var statusClass = await statusEl.First.GetAttributeAsync("class") ?? "";
                if (statusClass.Contains("currently"))
                    status = "ongoing";
                else if (statusClass.Contains("completed"))
                    status = "completed";
                else
                {
                    var statusText = (await statusEl.First.InnerTextAsync()).Trim().ToLowerInvariant();
                    if (statusText.Contains("emision") || statusText.Contains("emisión"))
                        status = "ongoing";
                    else if (statusText.Contains("finalizado"))
                        status = "completed";
                    else if (statusText.Contains("estrenar"))
                        status = "upcoming";
                }
            }

            // Type from li[rel="tipo"] text content
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

    // ── Episode list from series page ───────────────────────

    private async Task<IReadOnlyList<EpisodeScrapedData>> ScrapeEpisodesFromDetailAsync(
        string baseUrl, string slug, Guid seriesId, int delayMs, CancellationToken ct)
    {
        var result = new List<EpisodeScrapedData>();

        try
        {
            // JKAnime loads episode links dynamically inside div.capitulos.
            // Wait for episode links to appear (they have pattern /{slug}/{N}/).
            try
            {
                await Page.WaitForSelectorAsync("div.capitulos a[href]",
                    new PageWaitForSelectorOptions { Timeout = 10_000 });
            }
            catch (TimeoutException)
            {
                logger.LogDebug("No episode links loaded for {Slug} — may be upcoming", slug);
                return result;
            }

            var rows = await Page.Locator($"div.capitulos a[href*='/{slug}/']").AllAsync();

            foreach (var row in rows)
            {
                try
                {
                    var href = await row.GetAttributeAsync("href");
                    if (href is null) continue;

                    // Extract episode number from href: /{slug}/{N}/
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

    // ── Episode mirrors (extract from JS variables) ─────────
    //
    // JKAnime mechanism:
    //  - The episode page defines `var servers = [...]` with base64-encoded embed URLs
    //    for external providers (Streamwish, VOE, Vidhide, Mixdrop, Mp4upload, etc.).
    //  - Static servers (Desu, Magi, Xtreme S) use JKAnime's own player wrapper
    //    and don't have standard embed URLs.
    //  - OK.ru is embedded via `jkokru.php?u=ID` in the `video[]` array.
    //  - We extract URLs directly from JS evaluation — no tab clicking needed.

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
        // JKAnime defines `var servers = [{ remote: "base64url", server: "Name", ... }]`
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
        catch
        {
            return "unknown";
        }
    }
}
