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
            // PASS 1: Discover all series from directory pages
            // ═══════════════════════════════════════════════
            var discoveredSlugs = new List<string>();
            var seriesCount = 0;

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

                    // Check blocked slugs
                    var blocked = await db.BlockedSlugs.AnyAsync(b => b.Slug == item.Slug, ct);
                    if (blocked) continue;

                    var status = MapStatus(item.Status);
                    var type = MapType(item.Type);

                    var data = new SeriesScrapedData(
                        Slug: item.Slug.Trim(),
                        Title: item.Title.Trim(),
                        CoverUrl: item.Image,
                        Status: status,
                        Type: type,
                        Synopsis: string.IsNullOrWhiteSpace(item.Synopsis) ? null : item.Synopsis.Trim());

                    await upsert.UpsertSeriesAsync(data, ct);
                    discoveredSlugs.Add(item.Slug.Trim());
                    seriesCount++;
                }

                logger.LogDebug("Pass 1 — page {Page}/{MaxPages}: {Count} series", page, maxPages, animesPage.Data.Count);

                // Update progress every 5 pages
                if (page % 5 == 0)
                    await UpdateProgressAsync(job, $"pass1:page:{page}/{maxPages},discovered:{seriesCount}", ct);

                await JitterDelayAsync(delayMs, ct);
            }

            await UpdateProgressAsync(job, $"pass1:complete,discovered:{seriesCount},starting:pass2", ct);
            logger.LogInformation("Pass 1 complete — discovered {Count} series across directory pages", seriesCount);

            // ═══════════════════════════════════════════════
            // PASS 2: Enrich each series (detail + episodes + mirrors)
            // ═══════════════════════════════════════════════
            var enrichedCount = 0;
            var episodeTotal = 0;
            var mirrorTotal = 0;
            var skippedCount = 0;

            // Get all series that need enrichment (no synopsis = not yet enriched, OR not scraped in last 24h)
            var seriesToEnrich = await db.Series
                .AsNoTracking()
                .Where(s => discoveredSlugs.Contains(s.Slug))
                .Where(s => s.Synopsis == null || s.LastScrapedAt == null || s.LastScrapedAt < DateTime.UtcNow.AddHours(-24))
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
                $"complete:series:{seriesCount},enriched:{enrichedCount},episodes:{episodeTotal},mirrors:{mirrorTotal}",
                ct);

            logger.LogInformation(
                "Backfill complete — series={S}, enriched={E}, episodes={Ep}, mirrors={M}, skipped={Sk}",
                seriesCount, enrichedCount, episodeTotal, mirrorTotal, skippedCount);

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

    // ── Mirror extraction ───────────────────────────────────
    //
    // Resolves real embed URLs through JKAnime's jkplayer wrapper.
    // The episode page iframe points to jkanime.net/jkplayer/um?e=TOKEN (wrapper).
    // Inside that, JS decodes the token and creates an inner iframe with the real
    // embed (streamwish.com/e/..., voe.sx/e/..., etc.).
    // We use the Playwright Frame API to access the jkplayer frame and read
    // the inner iframe's src — the actual embeddable URL.

    private async Task<IReadOnlyList<MirrorScrapedData>> ScrapeEpisodeMirrorsAsync(
        string episodeUrl, Guid episodeId, int delayMs, CancellationToken ct)
    {
        var mirrors = new List<MirrorScrapedData>();
        var capturedEmbeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Network interception: capture real embed URLs from EXTERNAL domains only
        Page.RequestFinished += (_, req) =>
        {
            var url = req.Url;
            if (!url.StartsWith("http")) return;

            // Skip JKAnime internal URLs — these are wrappers, not real embeds
            if (url.Contains("jkanime.net", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("jkdesa.com", StringComparison.OrdinalIgnoreCase))
                return;

            // Skip static assets
            try
            {
                var path = new Uri(url).AbsolutePath;
                if (path.EndsWith(".js") || path.EndsWith(".css") || path.EndsWith(".png") ||
                    path.EndsWith(".jpg") || path.EndsWith(".gif") || path.EndsWith(".svg") ||
                    path.EndsWith(".ico") || path.EndsWith(".woff") || path.EndsWith(".woff2"))
                    return;
            }
            catch { return; }

            // Only capture URLs that look like embed pages
            if (url.Contains("/embed/", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("/e/", StringComparison.OrdinalIgnoreCase))
            {
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

        // Resolve the initial iframe → real embed (through jkplayer frame)
        var initialEmbed = await ResolveCurrentEmbedAsync(ct);
        if (initialEmbed is not null)
        {
            capturedEmbeds.Add(initialEmbed);
            logger.LogDebug("Initial embed resolved: {Src}", initialEmbed);
        }

        // Find all server/option tabs
        var serverTabs = await Page.Locator(
            "div.anime_muti_link ul li a, " +
            "div.anime_muti_link li a, " +
            "a.play-video, " +
            "li.server-item a, " +
            "div.custom_tab a, " +
            "div.player_option a, " +
            "div.video_options a").AllAsync();

        // Click each tab, wait for iframe src change, then resolve through jkplayer frame
        for (var i = 0; i < serverTabs.Count && i < 15; i++)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var beforeSrc = await Page.EvaluateAsync<string?>(
                    "(() => { const f = document.querySelector('iframe'); return f ? f.src : ''; })()") ?? "";

                await serverTabs[i].ClickAsync(new LocatorClickOptions { Timeout = 3000 });

                try
                {
                    var escapedSrc = beforeSrc.Replace("\\", "\\\\").Replace("'", "\\'");
                    await Page.WaitForFunctionAsync(
                        $"() => {{ const f = document.querySelector('iframe'); return f && f.src && f.src !== '' && f.src !== '{escapedSrc}'; }}",
                        new PageWaitForFunctionOptions { Timeout = 8_000 });

                    // Resolve through jkplayer frame to get real embed
                    var realEmbed = await ResolveCurrentEmbedAsync(ct);
                    if (realEmbed is not null)
                    {
                        capturedEmbeds.Add(realEmbed);
                        logger.LogDebug("Tab {Index}: resolved embed {Src}", i, realEmbed);
                    }
                    else
                    {
                        // Fallback: read raw iframe src (may be direct external embed for some servers)
                        var rawSrc = await Page.EvaluateAsync<string?>(
                            "(() => { const f = document.querySelector('iframe'); return f ? f.src : null; })()");
                        if (!string.IsNullOrWhiteSpace(rawSrc) && rawSrc.StartsWith("http") &&
                            !IsJkAnimeDomain(rawSrc))
                        {
                            capturedEmbeds.Add(rawSrc);
                            logger.LogDebug("Tab {Index}: direct embed {Src}", i, rawSrc);
                        }
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

        // Build mirror records — only from validated external embeds
        short priority = 0;
        foreach (var url in capturedEmbeds)
        {
            // Final safety check: never store JKAnime wrapper URLs
            if (IsJkAnimeDomain(url)) continue;

            var providerName = ExtractProviderName(url);
            mirrors.Add(new MirrorScrapedData(
                EpisodeId: episodeId,
                ProviderName: providerName,
                EmbedUrl: url,
                QualityLabel: 720,
                Priority: priority++));
        }

        logger.LogDebug("Episode {Url}: captured {Count} real mirrors from {Tabs} tabs",
            episodeUrl, mirrors.Count, serverTabs.Count);

        await JitterDelayAsync(delayMs, ct);
        return mirrors;
    }

    /// <summary>
    /// Resolves the current player's real embed URL by looking inside the jkplayer frame.
    /// JKAnime wraps all embeds in jkanime.net/jkplayer/um?e=TOKEN which decodes client-side
    /// and loads the real embed (streamwish, voe, etc.) in a nested iframe.
    /// </summary>
    private async Task<string?> ResolveCurrentEmbedAsync(CancellationToken ct)
    {
        // Brief wait for the frame to attach after iframe src change
        await Task.Delay(500, ct);

        // Find the jkplayer frame among page's sub-frames
        var jkFrame = Page.Frames.FirstOrDefault(f =>
            f.Url.Contains("jkplayer", StringComparison.OrdinalIgnoreCase) ||
            (f.Url.Contains("jkanime.net", StringComparison.OrdinalIgnoreCase) && f != Page.MainFrame));

        if (jkFrame is null)
        {
            // No jkplayer frame — maybe it's already a direct external embed
            var directSrc = await Page.EvaluateAsync<string?>(
                "(() => { const f = document.querySelector('iframe'); return f?.src || null; })()");
            if (!string.IsNullOrWhiteSpace(directSrc) && directSrc.StartsWith("http") &&
                !IsJkAnimeDomain(directSrc))
                return directSrc;

            return null;
        }

        try
        {
            // Wait for the real embed iframe to appear inside jkplayer
            await jkFrame.WaitForFunctionAsync(@"
                () => {
                    const f = document.querySelector('iframe');
                    return f && f.src && f.src.startsWith('http') &&
                           !f.src.includes('jkanime.net') && !f.src.includes('jkdesa.com') &&
                           !f.src.includes('jkplayer');
                }
            ", new FrameWaitForFunctionOptions { Timeout = 12_000 });

            return await jkFrame.EvaluateAsync<string?>(
                "(() => { const f = document.querySelector('iframe'); return f?.src || null; })()");
        }
        catch (TimeoutException)
        {
            logger.LogDebug("jkplayer frame did not resolve to a real embed within 12s");
            return null;
        }
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
                var h when h.Contains("streamwish") => "streamwish",
                var h when h.Contains("voe") => "voe",
                var h when h.Contains("desu") => "desu",
                var h when h.Contains("nozomi") => "nozomi",
                var h when h.Contains("mega") => "mega",
                var h when h.Contains("vidhide") => "vidhide",
                _ => host.Split('.')[0]
            };
        }
        catch { return "unknown"; }
    }
}
