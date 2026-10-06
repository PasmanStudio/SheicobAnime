using System.Diagnostics;
using System.Xml.Linq;
using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Scraper.Infrastructure.AiRewrite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>
/// Prueba local de la escalera del video EMBEBIDO, sin DB ni Instagram:
/// toma las últimas N notas del feed de Crunchyroll, lee sus embeds de la API
/// de stories, los valida y baja con el mismo TrailerDownloadService de prod y
/// renderiza el reel con InstagramVideoService. Deja los MP4 en
/// <c>outDir</c> (por defecto el temp del sistema, nunca dentro del repo ni
/// como artifact de CI: el repo es público) y una tabla por consola. Corre desde la IP de quien lo
/// ejecuta — no reproduce el bot-check de los runners (eso es yt-diag.yml).
///
/// Usage: dotnet run --project scraper/AnimeIndex.Scraper -- --video-probe [N] [outDir]
/// </summary>
public static class VideoProbe
{
    private const string FeedUrl = "https://cr-news-api-service.prd.crunchyrollsvc.com/v1/es-419/rss";

    public static async Task RunAsync(
        IHttpClientFactory httpFactory, ILoggerFactory loggers, int count, string outDir, CancellationToken ct = default)
    {
        var log = loggers.CreateLogger("VideoProbe");
        // Render local: las máquinas de desarrollo tardan bastante más que el runner
        var settings = new InstagramSettings { FfmpegTimeoutMinutes = 15 };
        // En CI el workflow pasa el proxy de WARP, las cookies y los clientes
        // de yt-dlp por env (Instagram__*), igual que a --news.
        new ConfigurationBuilder().AddEnvironmentVariables().Build().GetSection("Instagram").Bind(settings);
        // El render es lo caro: se prueba con el primer clip de YouTube y el
        // primero de X; el resto solo valida y baja.
        var renderedYouTube = false;
        var renderedX = false;
        var trailers = new TrailerDownloadService(settings, loggers.CreateLogger<TrailerDownloadService>());
        var video = new InstagramVideoService(settings, loggers.CreateLogger<InstagramVideoService>());
        var images = new AnimeNewsImageService(httpFactory, loggers.CreateLogger<AnimeNewsImageService>(), settings);
        var http = httpFactory.CreateClient();
        Directory.CreateDirectory(outDir);

        XNamespace media = "http://search.yahoo.com/mrss/";
        var feed = XDocument.Parse(await http.GetStringAsync(FeedUrl, ct));
        var items = feed.Descendants("item").Take(count).Select(i => new
        {
            Title = (string?)i.Element("title") ?? "",
            Link = (string?)i.Element("link") ?? "",
            Image = (string?)i.Element(media + "thumbnail")?.Attribute("url"),
        }).ToList();

        var rows = new List<string>();
        var n = 0;
        foreach (var it in items)
        {
            n++;
            var sw = Stopwatch.StartNew();
            var api = AnimeNewsFeedService.CrunchyrollStoryApiUrl(it.Link);
            string status;
            try
            {
                var body = api is null ? "" : await http.GetStringAsync(api, ct);
                var videoUrls = AnimeNewsFeedService.ExtractArticleVideoUrls(body);
                var tweetUrl = AnimeNewsFeedService.ExtractArticleTweetUrl(body);

                var candidates = new List<TrailerCandidate>();
                foreach (var url in videoUrls)
                    if (await trailers.ValidateAsync(url, requireSpanish: false, trustProvenance: true, ct: ct) is { } c)
                        candidates.Add(c);
                if (tweetUrl is not null
                    && await trailers.ValidateOfficialPostAsync(
                        tweetUrl, TrailerDownloadService.SubjectFromTitle(it.Title), requireTrustSignal: false, ct) is { } t)
                    candidates.Add(t);

                if (videoUrls.Count == 0 && tweetUrl is null)
                {
                    status = "SIN EMBED";
                }
                else if (candidates.Count == 0)
                {
                    status = $"EMBED NO VÁLIDO ({string.Join(" ", videoUrls.Append(tweetUrl ?? ""))})";
                }
                else
                {
                    (TrailerCandidate C, string Path)? got = null;
                    foreach (var c in candidates)
                        if (await trailers.DownloadAsync(c.Url, ct) is { } p) { got = (c, p); break; }

                    if (got is null)
                    {
                        status = $"NO BAJÓ ({string.Join(" ", candidates.Select(c => c.Url))})";
                    }
                    else
                    {
                        var (cand, path) = got.Value;
                        var mb = new FileInfo(path).Length / 1048576.0;
                        var isYouTube = TrailerDownloadService.IsYouTube(cand.Url);
                        var render = isYouTube ? !renderedYouTube : !renderedX;
                        try
                        {
                            status = $"BAJÓ {cand.Url} ({cand.DurationSeconds:F0}s, {mb:F1} MB)";
                            if (render)
                            {
                                if (isYouTube) renderedYouTube = true; else renderedX = true;

                                var item = new AnimeNewsItem
                                {
                                    Id = Guid.NewGuid(), SourceKey = "crunchyroll", RssGuid = it.Link,
                                    Title = it.Title, ArticleUrl = it.Link, ImageUrl = it.Image,
                                    PublishedAt = DateTime.UtcNow, FetchedAt = DateTime.UtcNow,
                                };
                                var content = new NewsContent(it.Title, null, [], "", [], FromAi: false);
                                var imgs = it.Image is null ? new List<string>() : [it.Image];
                                var slides = await images.GenerateReelSlidesAsync(item, content, imgs, maxKeyPoints: 0, ct: ct);
                                var (hook, overlay) = images.GenerateVideoReelLayers(content);
                                var reel = await video.GenerateTrailerReelAsync(
                                    path, hook, overlay, slides.Skip(1).ToList(), cand.DurationSeconds, cand.SubtitlesPath, ct);
                                var outFile = Path.Combine(outDir, $"probe-{n:00}.mp4");
                                await File.WriteAllBytesAsync(outFile, reel.Mp4, ct);
                                status = $"BAJÓ Y RENDERIZÓ {cand.Url} ({cand.DurationSeconds:F0}s, {mb:F1} MB) → reel {reel.DurationSeconds:F1}s {reel.Mp4.Length / 1048576.0:F1} MB → {Path.GetFileName(outFile)}";
                            }
                        }
                        catch (Exception ex)
                        {
                            status = $"BAJÓ PERO EL RENDER FALLÓ: {ex.Message}";
                        }
                        finally
                        {
                            TrailerDownloadService.CleanUp(path);
                            if (cand.SubtitlesPath is not null) TrailerDownloadService.CleanUp(cand.SubtitlesPath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                status = $"ERROR: {ex.Message}";
            }

            var row = $"{n:00} [{sw.Elapsed.TotalSeconds,5:F0}s] {Trim(it.Title, 60),-60} {status}";
            log.LogInformation("{Row}", row);
            rows.Add(row);
        }

        Console.WriteLine();
        Console.WriteLine("──── RESUMEN ────");
        foreach (var r in rows) Console.WriteLine(r);
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
