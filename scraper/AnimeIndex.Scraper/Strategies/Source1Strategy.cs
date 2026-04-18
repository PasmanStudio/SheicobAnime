using AnimeIndex.Api.Data;
using AnimeIndex.Api.Infrastructure.Scraping;
using AnimeIndex.Scraper.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace AnimeIndex.Scraper.Strategies;

/// <summary>
/// AnimeFlv scraper (www4.animeflv.net).
/// Browse /browse with pagination → series detail → episode pages → iframe extraction.
/// </summary>
public sealed class Source1Strategy(
    AppDbContext db,
    MirrorProbeService probe,
    UpsertPipelineService upsert,
    IConfiguration config,
    ILogger<Source1Strategy> logger) : PlaywrightBase, IScrapeStrategy
{
    public string SourceKey => "source1";

    public async Task<ScrapeResult> ScrapeAsync(Guid scrapeJobId, CancellationToken ct = default)
    {
        var baseUrl = config["Source1:BaseUrl"]
            ?? throw new InvalidOperationException("Source1:BaseUrl is not configured.");
        var delayMs = config.GetValue("Source1:DelayMs", 2000);
        var maxPages = config.GetValue("Source1:MaxPages", 5);

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

        logger.LogInformation("AnimeFlvStrategy starting — baseUrl={BaseUrl}, maxPages={MaxPages}", baseUrl, maxPages);

        // ── Discover series from /browse pages ────────────────
        var discovered = await DiscoverSeriesAsync(baseUrl, maxPages, delayMs, ct);

        if (discovered.Count == 0)
            logger.LogWarning("AnimeFlv returned 0 series — likely blocked by Cloudflare or domain changed. Verify {BaseUrl}/browse is accessible.", baseUrl);

        foreach (var series in discovered)
        {
            if (ct.IsCancellationRequested) break;

            if (await CheckCircuitBreakerAsync(ct))
                logger.LogWarning("Circuit breaker tripped — paused 10 min");

            // Re-check blocked slug for each discovered series
            var blocked = await db.BlockedSlugs.AnyAsync(b => b.Slug == series.Slug, ct);
            if (blocked)
            {
                logger.LogDebug("Skipping blocked slug {Slug}", series.Slug);
                continue;
            }

            // ── Skip completed series that already have episodes ──
            var existing = await db.Series
                .Where(s => s.Slug == series.Slug)
                .Select(s => new { s.Id, s.Status, EpisodeCount = s.Episodes.Count })
                .FirstOrDefaultAsync(ct);

            if (existing is { Status: "completed", EpisodeCount: > 0 })
            {
                logger.LogDebug("Skipping completed series {Slug} — already has {Count} episodes",
                    series.Slug, existing.EpisodeCount);
                seriesCount++;
                continue;
            }

            // Get full series detail (synopsis, genres, score, etc.)
            var detail = await ScrapeSeriesDetailAsync(baseUrl, series.Slug, delayMs, ct);
            var enriched = detail ?? series;

            var seriesId = await upsert.UpsertSeriesAsync(enriched, ct);
            seriesCount++;

            // ── Discover episodes ─────────────────────────────
            var episodes = await ScrapeEpisodesFromDetailAsync(seriesId, ct);

            foreach (var ep in episodes)
            {
                if (ct.IsCancellationRequested) break;

                var episodeId = await upsert.UpsertEpisodeAsync(ep, ct);
                episodeCount++;

                // Navigate to episode page to extract mirror iframes
                var epUrl = $"{baseUrl.TrimEnd('/')}/ver/{series.Slug}-{ep.EpisodeNumber}";
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
            "AnimeFlvStrategy complete — series={S} episodes={E} mirrors={M}",
            seriesCount, episodeCount, mirrorCount);

        return new ScrapeResult(true,
            SeriesIndexed: seriesCount,
            EpisodesIndexed: episodeCount,
            MirrorsIndexed: mirrorCount);
    }

    // ── Directory browsing ──────────────────────────────────

    private async Task<IReadOnlyList<SeriesScrapedData>> DiscoverSeriesAsync(
        string baseUrl, int maxPages, int delayMs, CancellationToken ct)
    {
        var result = new List<SeriesScrapedData>();

        for (var page = 1; page <= maxPages; page++)
        {
            if (ct.IsCancellationRequested) break;
            if (await CheckCircuitBreakerAsync(ct)) break;

            var url = $"{baseUrl.TrimEnd('/')}/browse?page={page}";
            if (!await GoToAsync(url, ct))
            {
                logger.LogWarning("Cannot reach {Url}", url);
                break;
            }

            // AnimeFlv /browse: each card is <article class="Anime"> or <li>
            // containing <a href="/anime/{slug}"> and <h3>{title}</h3>
            var cards = await Page.Locator("ul.ListAnimes li article.Anime").AllAsync();
            if (cards.Count == 0)
            {
                // Diagnostic: log page title and body snippet to diagnose CF blocks / DOM changes
                var pageTitle = await Page.TitleAsync();
                var bodySnippet = await Page.Locator("body").First.InnerTextAsync();
                if (bodySnippet.Length > 500) bodySnippet = bodySnippet[..500];
                logger.LogWarning(
                    "No cards found on page {Page} — title={Title}, body={Body}",
                    page, pageTitle, bodySnippet);
                break;
            }

            foreach (var card in cards)
            {
                try
                {
                    var linkEl = card.Locator("a").First;
                    var href = await linkEl.GetAttributeAsync("href");
                    if (string.IsNullOrWhiteSpace(href)) continue;

                    // Extract slug from /anime/{slug}
                    var slug = href.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                    if (string.IsNullOrWhiteSpace(slug)) continue;

                    var title = await card.Locator("h3.Title").InnerTextAsync();
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    // Cover image from <figure><img>
                    string? coverUrl = null;
                    var imgEl = card.Locator("figure img").First;
                    if (await imgEl.CountAsync() > 0)
                    {
                        coverUrl = await imgEl.GetAttributeAsync("src");
                        if (coverUrl is not null && !coverUrl.StartsWith("http"))
                            coverUrl = $"{baseUrl.TrimEnd('/')}{coverUrl}";
                    }

                    // Type badge (TV, OVA, etc.)
                    string? type = null;
                    var typeBadge = card.Locator("span.Type");
                    if (await typeBadge.CountAsync() > 0)
                    {
                        var raw = (await typeBadge.InnerTextAsync()).Trim().ToLowerInvariant();
                        type = raw switch
                        {
                            "anime" or "tv" => "tv",
                            "película" or "pelicula" or "movie" => "movie",
                            "ova" => "ova",
                            "ona" => "ona",
                            "especial" or "special" => "special",
                            _ => "tv"
                        };
                    }

                    result.Add(new SeriesScrapedData(
                        Slug: slug.Trim(),
                        Title: title.Trim(),
                        CoverUrl: coverUrl,
                        Status: "ongoing",
                        Type: type ?? "tv"));
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

    // ── Series detail page ──────────────────────────────────

    private async Task<SeriesScrapedData?> ScrapeSeriesDetailAsync(
        string baseUrl, string slug, int delayMs, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/anime/{slug}";
        if (!await GoToAsync(url, ct))
        {
            logger.LogWarning("Cannot reach series detail {Url}", url);
            return null;
        }

        try
        {
            var title = await Page.Locator("h1.Title").InnerTextAsync();

            // Synopsis
            string? synopsis = null;
            var synopsisEl = Page.Locator("div.Description p");
            if (await synopsisEl.CountAsync() > 0)
                synopsis = (await synopsisEl.First.InnerTextAsync()).Trim();

            // Cover
            string? coverUrl = null;
            var coverImg = Page.Locator("div.AnimeCover img, div.Image img").First;
            if (await coverImg.CountAsync() > 0)
            {
                coverUrl = await coverImg.GetAttributeAsync("src");
                if (coverUrl is not null && !coverUrl.StartsWith("http"))
                    coverUrl = $"{baseUrl.TrimEnd('/')}{coverUrl}";
            }

            // Genres from <nav class="Nvgnrs"><a>
            var genres = new List<string>();
            var genreLinks = await Page.Locator("nav.Nvgnrs a").AllAsync();
            foreach (var g in genreLinks)
            {
                var genreName = (await g.InnerTextAsync()).Trim();
                if (!string.IsNullOrWhiteSpace(genreName))
                    genres.Add(genreName);
            }

            // Status from <span class="fa-tv"> text or info section
            string? status = null;
            var infoSpans = await Page.Locator("p.AnmStts span").AllAsync();
            foreach (var span in infoSpans)
            {
                var text = (await span.InnerTextAsync()).Trim().ToLowerInvariant();
                status = text switch
                {
                    "en emision" or "en emisión" => "ongoing",
                    "finalizado" => "completed",
                    "próximamente" or "proximamente" => "upcoming",
                    _ => status
                };
            }

            // Type
            string? type = null;
            var typeEl = Page.Locator("span.Type");
            if (await typeEl.CountAsync() > 0)
            {
                var raw = (await typeEl.First.InnerTextAsync()).Trim().ToLowerInvariant();
                type = raw switch
                {
                    "anime" or "tv" => "tv",
                    "película" or "pelicula" or "movie" => "movie",
                    "ova" => "ova",
                    "ona" => "ona",
                    "especial" or "special" => "special",
                    _ => "tv"
                };
            }

            // Score
            decimal? score = null;
            var scoreEl = Page.Locator("#votes_pr498, span.vtprmd");
            if (await scoreEl.CountAsync() > 0)
            {
                var scoreText = (await scoreEl.First.InnerTextAsync()).Trim();
                if (decimal.TryParse(scoreText, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    score = parsed;
            }

            await JitterDelayAsync(delayMs, ct);

            return new SeriesScrapedData(
                Slug: slug,
                Title: title.Trim(),
                CoverUrl: coverUrl,
                Status: status ?? "ongoing",
                Type: type ?? "tv",
                Synopsis: synopsis,
                Score: score,
                Genres: genres.Count > 0 ? genres : null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse series detail for {Slug}", slug);
            return null;
        }
    }

    // ── Episode list from series page (already navigated) ───

    private async Task<IReadOnlyList<EpisodeScrapedData>> ScrapeEpisodesFromDetailAsync(
        Guid seriesId, CancellationToken ct)
    {
        var result = new List<EpisodeScrapedData>();

        try
        {
            // AnimeFlv episode list: <ul id="episodeList"> with <li>
            // Each <li> has <a href="/ver/{slug}-{ep}"> and episode number
            var rows = await Page.Locator("ul.ListCaps li, ul#episodeList li").AllAsync();

            foreach (var row in rows)
            {
                try
                {
                    var linkEl = row.Locator("a").First;
                    var href = await linkEl.GetAttributeAsync("href");
                    if (string.IsNullOrWhiteSpace(href)) continue;

                    // Try to get episode number from <p> inside the row
                    var epNumEl = row.Locator("p");
                    string? epNumText = null;
                    if (await epNumEl.CountAsync() > 0)
                        epNumText = (await epNumEl.First.InnerTextAsync()).Trim();

                    // Or extract from href: /ver/{slug}-{N}
                    short epNum = 0;
                    if (epNumText is not null && short.TryParse(
                        System.Text.RegularExpressions.Regex.Match(epNumText, @"\d+").Value,
                        out var parsed))
                    {
                        epNum = parsed;
                    }
                    else
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(href, @"-(\d+)$");
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
                    logger.LogDebug(ex, "Failed to parse episode row");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to extract episode list");
        }

        return result;
    }

    // ── Episode mirrors (iframe extraction) ─────────────────
    //
    // AnimeFlv mechanism:
    //  - Episode pages load a default video player iframe.
    //  - Server tabs reveal additional mirrors via AJAX → iframe src update.
    //  - We use WaitForFunctionAsync to detect iframe src changes precisely
    //    (completes as soon as AJAX finishes, no wasted time).
    //  - Network interception for /embed/ and /e/ as fallback for servers that
    //    inject iframes through alternate JS patterns.

    private async Task<IReadOnlyList<MirrorScrapedData>> ScrapeEpisodeMirrorsAsync(
        string episodeUrl, Guid episodeId, int delayMs, CancellationToken ct)
    {
        var mirrors = new List<MirrorScrapedData>();
        var capturedEmbeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Network interception: capture embed/player URLs as fallback
        Page.RequestFinished += (_, req) =>
        {
            var url = req.Url;
            if (url.Contains("/embed/", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("/e/", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("player", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("stream", StringComparison.OrdinalIgnoreCase))
            {
                if (url.StartsWith("http"))
                    capturedEmbeds.Add(url);
            }
        };

        if (!await GoToAsync(episodeUrl, ct))
        {
            logger.LogWarning("Cannot reach episode {Url}", episodeUrl);
            return mirrors;
        }

        // Wait for the initial player iframe to appear
        try
        {
            await Page.WaitForSelectorAsync("iframe",
                new PageWaitForSelectorOptions { Timeout = 12_000 });
        }
        catch (TimeoutException)
        {
            logger.LogDebug("No iframe found on {Url}", episodeUrl);
            return mirrors;
        }

        // Capture the initial iframe src (default server)
        var initialSrc = await Page.EvaluateAsync<string?>(
            "(() => { const f = document.querySelector('iframe'); return f ? f.src : null; })()");
        if (!string.IsNullOrWhiteSpace(initialSrc) && initialSrc.StartsWith("http"))
        {
            capturedEmbeds.Add(initialSrc);
            logger.LogDebug("Initial iframe src: {Src}", initialSrc);
        }

        // Find all server/option tabs — AnimeFlv uses various server tab patterns
        var serverTabs = await Page.Locator(
            "li.Tab, li.server-option, .CapiTnv li, " +
            "div.anime_muti_link ul li a, " +
            "a.play-video, " +
            "div.player_option a").AllAsync();

        logger.LogDebug("Found {Count} server tabs on {Url}", serverTabs.Count, episodeUrl);

        // Click each tab and wait for iframe src to change via AJAX
        for (var i = 0; i < serverTabs.Count && i < 15; i++)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                // Record current iframe src before clicking
                var beforeSrc = await Page.EvaluateAsync<string?>(
                    "(() => { const f = document.querySelector('iframe'); return f ? f.src : ''; })()") ?? "";

                // Click the server tab
                await serverTabs[i].ClickAsync(new LocatorClickOptions { Timeout = 3000 });

                // Wait for iframe src to change (AJAX response updates it)
                try
                {
                    var escapedSrc = beforeSrc.Replace("\\", "\\\\").Replace("'", "\\'");
                    await Page.WaitForFunctionAsync(
                        $"() => {{ const f = document.querySelector('iframe'); return f && f.src && f.src !== '' && f.src !== '{escapedSrc}'; }}",
                        new PageWaitForFunctionOptions { Timeout = 8_000 });

                    var newSrc = await Page.EvaluateAsync<string?>(
                        "(() => { const f = document.querySelector('iframe'); return f ? f.src : null; })()");
                    if (!string.IsNullOrWhiteSpace(newSrc) && newSrc.StartsWith("http"))
                    {
                        capturedEmbeds.Add(newSrc);
                        logger.LogDebug("Tab {Index}: captured {Src}", i, newSrc);
                    }
                }
                catch (TimeoutException)
                {
                    logger.LogDebug("Tab {Index}: iframe src didn't change within 8s on {Url}", i, episodeUrl);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed clicking server tab {Index} on {Url}", i, episodeUrl);
            }
        }

        // Deduplicate and create mirror records (sorted by provider quality)
        foreach (var url in capturedEmbeds)
        {
            var providerName = ExtractProviderName(url);
            if (BlockedProviders.Contains(providerName)) continue;

            mirrors.Add(new MirrorScrapedData(
                EpisodeId: episodeId,
                ProviderName: providerName,
                EmbedUrl: url,
                QualityLabel: 720,
                Priority: GetProviderPriority(providerName)));
        }

        logger.LogDebug("Episode {Url}: captured {Count} mirrors from {Tabs} tabs",
            episodeUrl, mirrors.Count, serverTabs.Count);

        await JitterDelayAsync(delayMs, ct);
        return mirrors;
    }

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

    private static string ExtractProviderName(string url)
    {
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            // Strip www. prefix and known CDN subdomains
            if (host.StartsWith("www.")) host = host[4..];

            // Map known embed domains to clean names
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
                var h when h.Contains("vidhide") => "vidhide",
                var h when h.Contains("mega") => "mega",
                var h when h.Contains("nozomi") => "nozomi",
                var h when h.Contains("desu") => "desu",
                var h when h.Contains("youtube") => "youtube",
                _ => host.Split('.')[0]
            };
        }
        catch
        {
            return "unknown";
        }
    }
}
