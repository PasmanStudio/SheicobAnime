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
        }

        logger.LogInformation(
            "JKAnimeStrategy complete — series={S} episodes={E} mirrors={M}",
            seriesCount, episodeCount, mirrorCount);

        return new ScrapeResult(true,
            SeriesIndexed: seriesCount,
            EpisodesIndexed: episodeCount,
            MirrorsIndexed: mirrorCount);
    }

    // ── Directory browsing (/directorio) ────────────────────

    private async Task<IReadOnlyList<SeriesScrapedData>> DiscoverSeriesAsync(
        string baseUrl, int maxPages, int delayMs, CancellationToken ct)
    {
        var result = new List<SeriesScrapedData>();

        for (var page = 1; page <= maxPages; page++)
        {
            if (ct.IsCancellationRequested) break;
            if (await CheckCircuitBreakerAsync(ct)) break;

            var url = $"{baseUrl.TrimEnd('/')}/directorio/{page}/";
            if (!await GoToAsync(url, ct))
            {
                logger.LogWarning("Cannot reach {Url}", url);
                break;
            }

            // JKAnime /directorio: cards with <div class="anime__item">
            // or <div class="card"> containing <a href="/{slug}/">
            var cards = await Page.Locator("div.anime__item, div.card, div.custom_item").AllAsync();
            if (cards.Count == 0)
            {
                logger.LogDebug("No cards found on page {Page} — end of directory", page);
                break;
            }

            foreach (var card in cards)
            {
                try
                {
                    var linkEl = card.Locator("a").First;
                    var href = await linkEl.GetAttributeAsync("href");
                    if (string.IsNullOrWhiteSpace(href)) continue;

                    // Extract slug from /{slug}/
                    var segments = href.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var slug = segments.LastOrDefault();
                    if (string.IsNullOrWhiteSpace(slug)) continue;

                    // Title from heading or link text
                    string? title = null;
                    var titleEl = card.Locator("h5, h4, h3, .title").First;
                    if (await titleEl.CountAsync() > 0)
                        title = (await titleEl.InnerTextAsync()).Trim();
                    title ??= (await linkEl.InnerTextAsync()).Trim();

                    if (string.IsNullOrWhiteSpace(title)) continue;

                    // Cover image
                    string? coverUrl = null;
                    var imgEl = card.Locator("img").First;
                    if (await imgEl.CountAsync() > 0)
                    {
                        coverUrl = await imgEl.GetAttributeAsync("src")
                            ?? await imgEl.GetAttributeAsync("data-src");
                    }

                    result.Add(new SeriesScrapedData(
                        Slug: slug.Trim(),
                        Title: title,
                        CoverUrl: coverUrl,
                        Status: "ongoing",
                        Type: "tv"));
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to parse a card on page {Page}", page);
                }
            }

            logger.LogDebug("Page {Page}: discovered {Count} series", page, cards.Count);
            await JitterDelayAsync(delayMs, ct);
        }

        return result;
    }

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
            // Title
            var title = await Page.Locator("h1, h2.title_anime, .anime__details__title h3").First.InnerTextAsync();

            // Synopsis
            string? synopsis = null;
            var synopsisEl = Page.Locator("p.sinopsis, .anime__details__text p, div.sinopsis");
            if (await synopsisEl.CountAsync() > 0)
                synopsis = (await synopsisEl.First.InnerTextAsync()).Trim();

            // Cover from CDN pattern: cdn.jkdesa.com/assets/images/animes/image/{slug}.jpg
            var coverUrl = $"https://cdn.jkdesa.com/assets/images/animes/image/{slug}.jpg";

            // Genres from genre links
            var genres = new List<string>();
            var genreLinks = await Page.Locator("span.anime__details__genre a, .generos a, a.btn-genero").AllAsync();
            foreach (var g in genreLinks)
            {
                var genreName = (await g.InnerTextAsync()).Trim();
                if (!string.IsNullOrWhiteSpace(genreName))
                    genres.Add(genreName);
            }

            // Status from info list
            string? status = null;
            string? type = null;
            var infoItems = await Page.Locator("ul.anime__details__widget li, .anime-type-peli li, span.info-value").AllAsync();
            foreach (var item in infoItems)
            {
                var text = (await item.InnerTextAsync()).Trim().ToLowerInvariant();

                if (text.Contains("estado"))
                {
                    if (text.Contains("emision") || text.Contains("emisión"))
                        status = "ongoing";
                    else if (text.Contains("finalizado"))
                        status = "completed";
                    else if (text.Contains("proximamente") || text.Contains("próximamente"))
                        status = "upcoming";
                }

                if (text.Contains("tipo"))
                {
                    if (text.Contains("tv")) type = "tv";
                    else if (text.Contains("película") || text.Contains("pelicula") || text.Contains("movie")) type = "movie";
                    else if (text.Contains("ova")) type = "ova";
                    else if (text.Contains("ona")) type = "ona";
                    else if (text.Contains("especial") || text.Contains("special")) type = "special";
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
            // JKAnime episode list: numbered links in episode navigation
            var rows = await Page.Locator("div.anime__pagination a, a.emark, div.ep-list a").AllAsync();

            foreach (var row in rows)
            {
                try
                {
                    var href = await row.GetAttributeAsync("href");
                    var text = (await row.InnerTextAsync()).Trim();

                    // Extract episode number from text or href (/{slug}/{N}/)
                    short epNum = 0;
                    if (short.TryParse(text, out var fromText))
                    {
                        epNum = fromText;
                    }
                    else if (href is not null)
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(href, @"/(\d+)/?$");
                        if (match.Success && short.TryParse(match.Groups[1].Value, out var fromHref))
                            epNum = fromHref;
                    }

                    if (epNum <= 0) continue;

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

    // ── Episode mirrors (Desu player iframe extraction) ─────

    private async Task<IReadOnlyList<MirrorScrapedData>> ScrapeEpisodeMirrorsAsync(
        string episodeUrl, Guid episodeId, int delayMs, CancellationToken ct)
    {
        var mirrors = new List<MirrorScrapedData>();
        var capturedEmbeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Capture dynamically injected iframe URLs
        Page.RequestFinished += (_, req) =>
        {
            if (req.Url.Contains("embed", StringComparison.OrdinalIgnoreCase) ||
                req.Url.Contains("player", StringComparison.OrdinalIgnoreCase) ||
                req.Url.Contains("stream", StringComparison.OrdinalIgnoreCase) ||
                req.Url.Contains("desu", StringComparison.OrdinalIgnoreCase))
                capturedEmbeds.Add(req.Url);
        };

        if (!await GoToAsync(episodeUrl, ct))
        {
            logger.LogWarning("Cannot reach episode {Url}", episodeUrl);
            return mirrors;
        }

        // Wait for Desu player JS injection
        try
        {
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions { Timeout = 15_000 });
        }
        catch (TimeoutException)
        {
            logger.LogDebug("NetworkIdle timeout for {Url} — proceeding", episodeUrl);
        }

        // Extract visible iframes
        var iframes = await Page.Locator("iframe").AllAsync();
        foreach (var iframe in iframes)
        {
            try
            {
                var src = await iframe.GetAttributeAsync("src");
                if (!string.IsNullOrWhiteSpace(src) && src.StartsWith("http"))
                    capturedEmbeds.Add(src);
            }
            catch { }
        }

        // Click server/quality tabs to reveal more mirrors
        var serverTabs = await Page.Locator("div.anime_muti_link a, a.play-video, li.server-item").AllAsync();
        for (var i = 0; i < serverTabs.Count && i < 6; i++)
        {
            try
            {
                await serverTabs[i].ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                await Task.Delay(1500, ct);

                var newIframes = await Page.Locator("iframe").AllAsync();
                foreach (var iframe in newIframes)
                {
                    var src = await iframe.GetAttributeAsync("src");
                    if (!string.IsNullOrWhiteSpace(src) && src.StartsWith("http"))
                        capturedEmbeds.Add(src);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed clicking server tab {Index}", i);
            }
        }

        short priority = 0;
        foreach (var url in capturedEmbeds)
        {
            var providerName = ExtractProviderName(url);

            mirrors.Add(new MirrorScrapedData(
                EpisodeId: episodeId,
                ProviderName: providerName,
                EmbedUrl: url,
                QualityLabel: 720,
                Priority: priority++));
        }

        await JitterDelayAsync(delayMs, ct);
        return mirrors;
    }

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
                var h when h.Contains("streamwish") => "streamwish",
                var h when h.Contains("voe") => "voe",
                var h when h.Contains("desu") => "desu",
                var h when h.Contains("nozomi") => "nozomi",
                var h when h.Contains("jkanime") => "jkanime",
                _ => host.Split('.')[0]
            };
        }
        catch
        {
            return "unknown";
        }
    }
}
