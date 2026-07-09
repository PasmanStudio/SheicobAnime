using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Scraper.Infrastructure.AiRewrite;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>
/// Generates sample Story and Feed images to disk for visual review.
/// Invoked via: dotnet run --project scraper/AnimeIndex.Scraper -- --images
/// Output: ./test-output/instagram/  (relative to working directory)
/// Generates both episode images AND news images.
/// </summary>
public static class TestImageGenerator
{
    // ── Episode samples ───────────────────────────────────────────────────────

    private static readonly (string Title, string Slug, string? CoverUrl, int Episode, string? EpTitle)[] EpisodeSamples =
    [
        (
            "Demon Slayer: Kimetsu no Yaiba",
            "demon-slayer",
            "https://cdn.myanimelist.net/images/anime/1286/99889.jpg",
            12,
            "El sonido del Tambor"
        ),
        (
            "Attack on Titan: The Final Season",
            "attack-on-titan",
            "https://cdn.myanimelist.net/images/anime/1948/120625.jpg",
            8,
            null
        ),
        (
            "One Piece",
            "one-piece",
            "https://cdn.myanimelist.net/images/anime/6/73245.jpg",
            1110,
            "Nuevo Mundo — El Despertar"
        ),
    ];

    // ── News samples — títulos en español al estilo SomosKudasai ─────────────
    // Todas las noticias en español latinoamericano, todas con imagen.

    private static readonly (string Slug, string Title, string? Summary, string? ImageUrl)[] NewsSamples =
    [
        (
            "kudasai-cloverworks",
            "CloverWorks prepara grandes anuncios para el Anime Expo 2026 el próximo 4 de julio",
            // Multi-párrafo para simular content:encoded real de SomosKudasai
            "CloverWorks confirmó que presentará grandes novedades en el Anime Expo 2026 el próximo 4 de julio.\n\nSe esperan nuevos tráilers y visuales oficiales para The Fragrant Flower Blooms With Dignity T2, My Dress-Up Darling T3, Bocchi the Rock! T2 y Rascal Does Not Dream of a Dear Friend.\n\nAún no hay confirmación oficial sobre qué animes estarán presentes en el panel del estudio de animación.",
            // Imagen real de SomosKudasai CDN
            "https://cdn.somoskudasai.com/width=1280,height=720,quality=75,format=auto,fit=cover/2026/05/yakineko.jpg"
        ),
        (
            "kudasai-mha-mundial",
            "My Hero Academia se une a la Selección Japonesa de Fútbol para el Mundial 2026 con colección oficial",
            "La franquicia reveló una colaboración especial con Samurai Blue que incluye productos oficiales bajo licencia, preventa online disponible y tiendas pop-up en Japón desde el 2 de junio.",
            "https://cdn.myanimelist.net/images/anime/1171/109222.jpg"
        ),
        (
            "kudasai-rezero-s4",
            "Los fans de Re:Zero quedan impactados tras el capítulo 8 de la temporada 4 — Episodio recibe 9.9/10 en IMDb",
            "El episodio muestra a Subaru recordando a sus padres mientras va iniciando su colapso mental. Los fans elogiaron la dirección y animación del estudio.",
            "https://cdn.myanimelist.net/images/anime/1448/109314.jpg"
        ),
        (
            "kudasai-titulo-largo",
            "Ufotable anuncia oficialmente Fate/stay night: Heaven's Feel IV — Producción confirmada para el 2027",
            "El estudio confirmó que la cuarta película de la trilogía Heaven's Feel está en producción temprana, con una ventana de estreno tentativa para finales del 2027.",
            "https://cdn.myanimelist.net/images/anime/1813/95650.jpg"
        ),
    ];

    public static async Task RunAsync(
        IHttpClientFactory httpFactory, ILoggerFactory loggerFactory, NewsRewriteService rewriter)
    {
        var outDir = Path.Combine(Directory.GetCurrentDirectory(), "test-output", "instagram");
        Directory.CreateDirectory(outDir);

        var total = Stopwatch.StartNew();

        // ── Episode images ────────────────────────────────────────────────────
        Console.WriteLine($"\n═══ EPISODE IMAGES ({EpisodeSamples.Length * 2} files) ═══");
        var epLogger  = loggerFactory.CreateLogger<InstagramImageService>();
        var epService = new InstagramImageService(httpFactory, epLogger);

        foreach (var (title, slug, coverUrl, ep, epTitle) in EpisodeSamples)
        {
            var series  = MakeSeries(title, slug, coverUrl);
            var episode = MakeEpisode(series.Id, ep, epTitle);

            var sw = Stopwatch.StartNew();
            var feedBytes = await epService.GenerateFeedAsync(series, episode);
            sw.Stop();
            var feedPath = Path.Combine(outDir, $"{slug}-ep{ep}-feed.jpg");
            await File.WriteAllBytesAsync(feedPath, feedBytes);
            Console.WriteLine($"  [feed ] {title} ep{ep}  →  {feedBytes.Length / 1024} KB  ({sw.ElapsedMilliseconds} ms)");

            sw.Restart();
            var storyBytes = await epService.GenerateStoryAsync(series, episode);
            sw.Stop();
            var storyPath = Path.Combine(outDir, $"{slug}-ep{ep}-story.jpg");
            await File.WriteAllBytesAsync(storyPath, storyBytes);
            Console.WriteLine($"  [story] {title} ep{ep}  →  {storyBytes.Length / 1024} KB  ({sw.ElapsedMilliseconds} ms)");
        }

        // ── News carousels ──────────────────────────────────────────────────────
        Console.WriteLine($"\n═══ NEWS CAROUSELS ({NewsSamples.Length} items) ═══");
        var newsLogger  = loggerFactory.CreateLogger<AnimeNewsImageService>();
        var newsService = new AnimeNewsImageService(httpFactory, newsLogger);

        foreach (var (slug, title, summary, imageUrl) in NewsSamples)
        {
            var newsItem = MakeNewsItem(title, summary, imageUrl);
            var content  = await rewriter.RewriteAsync(newsItem);

            var sw = Stopwatch.StartNew();
            var slides = await newsService.GenerateCarouselSlidesAsync(newsItem, content, []);
            sw.Stop();
            for (var i = 0; i < slides.Count; i++)
                await File.WriteAllBytesAsync(
                    Path.Combine(outDir, $"news-{slug}-slide{i + 1}.jpg"), slides[i]);
            Console.WriteLine(
                $"  [carousel {slides.Count} slides] {title[..Math.Min(title.Length, 55)]}  →  " +
                $"{slides.Sum(s => s.Length) / 1024} KB  ({sw.ElapsedMilliseconds} ms)");

            sw.Restart();
            var storyBytes = await newsService.GenerateStoryAsync(newsItem, content, []);
            sw.Stop();
            await File.WriteAllBytesAsync(Path.Combine(outDir, $"news-{slug}-story.jpg"), storyBytes);
            Console.WriteLine($"  [story] {storyBytes.Length / 1024} KB  ({sw.ElapsedMilliseconds} ms)");

            // Show the caption that would be posted (first sample only)
            if (slug == "kudasai-cloverworks")
            {
                Console.WriteLine("\n  ── Caption preview ──────────────────────────");
                foreach (var line in BuildPreviewCaption(content).Split('\n'))
                    Console.WriteLine($"  {line}");
                Console.WriteLine("  ─────────────────────────────────────────────\n");

                // ── Piezas del reel (crédito de música EN el video) ──
                const string sampleCredit =
                    "Música: Hyperfun — Kevin MacLeod (incompetech.com) · CC BY 4.0";

                sw.Restart();
                var reelSlides = await newsService.GenerateReelSlidesAsync(
                    newsItem, content, [], maxKeyPoints: 3, musicCredit: sampleCredit);
                sw.Stop();
                for (var i = 0; i < reelSlides.Count; i++)
                    await File.WriteAllBytesAsync(
                        Path.Combine(outDir, $"news-{slug}-reel-slide{i + 1}.jpg"), reelSlides[i]);
                Console.WriteLine($"  [reel slides ×{reelSlides.Count}, crédito CC en la última]  ({sw.ElapsedMilliseconds} ms)");

                var (trailerBg, trailerOv) = newsService.GenerateVideoReelLayers(content, sampleCredit);
                await File.WriteAllBytesAsync(Path.Combine(outDir, $"news-{slug}-trailer-bg.jpg"), trailerBg);
                await File.WriteAllBytesAsync(Path.Combine(outDir, $"news-{slug}-trailer-overlay.png"), trailerOv);
                Console.WriteLine("  [trailer-reel layers: bg + overlay con crédito]");
            }
        }

        // ── Real RSS item from SomosKudasai (live fetch) ─────────────────
        Console.WriteLine("\n═══ REAL RSS ITEM (SomosKudasai live) ═══");
        try
        {
            var dbLogger  = loggerFactory.CreateLogger<AnimeIndex.Scraper.Infrastructure.AnimeNewsFeedService>();
            var feedSvc   = new RealFeedFetcher(httpFactory, dbLogger);
            var (liveItem, liveImages) = await feedSvc.FetchLatestItemAsync();

            if (liveItem is not null)
            {
                var content = await rewriter.RewriteAsync(liveItem);

                var sw = Stopwatch.StartNew();
                var slides = await newsService.GenerateCarouselSlidesAsync(liveItem, content, liveImages);
                sw.Stop();
                for (var i = 0; i < slides.Count; i++)
                    await File.WriteAllBytesAsync(Path.Combine(outDir, $"real-slide{i + 1}.jpg"), slides[i]);
                Console.WriteLine($"  [carousel {slides.Count} slides] {liveItem.Title[..Math.Min(liveItem.Title.Length, 70)]}");
                Console.WriteLine($"  Image: {liveItem.ImageUrl?[..Math.Min(liveItem.ImageUrl?.Length ?? 0, 60)]}");
                Console.WriteLine($"  Size: {slides.Sum(s => s.Length) / 1024} KB  ({sw.ElapsedMilliseconds} ms)");

                sw.Restart();
                var storyBytes = await newsService.GenerateStoryAsync(liveItem, content, liveImages);
                sw.Stop();
                await File.WriteAllBytesAsync(Path.Combine(outDir, "real-story.jpg"), storyBytes);
                Console.WriteLine($"  [story] {storyBytes.Length / 1024} KB  ({sw.ElapsedMilliseconds} ms)");

                Console.WriteLine("\n  ── Caption ─────────────────────────────────────");
                foreach (var line in BuildPreviewCaption(content).Split('\n').Take(24))
                    Console.WriteLine($"  {line}");
                Console.WriteLine("  ────────────────────────────────────────────────\n");
            }
            else
            {
                Console.WriteLine("  (could not fetch live item — check network)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Live fetch failed: {ex.Message}");
        }

        total.Stop();
        Console.WriteLine($"\nDone in {total.ElapsedMilliseconds} ms total.");
        Console.WriteLine($"Open: {outDir}\n");

        // Auto-open the output folder on Windows
        if (OperatingSystem.IsWindows())
        {
            try { System.Diagnostics.Process.Start("explorer.exe", outDir); }
            catch { /* best-effort */ }
        }
    }

    // ── Minimal live RSS fetcher for --images testing ─────────────────────────

    private sealed class RealFeedFetcher(
        IHttpClientFactory httpFactory,
        ILogger<AnimeIndex.Scraper.Infrastructure.AnimeNewsFeedService> logger)
    {
        private static readonly System.Xml.Linq.XNamespace Media =
            "http://search.yahoo.com/mrss/";

        public async Task<(AnimeIndex.Api.Data.Entities.AnimeNewsItem? item, List<string> images)> FetchLatestItemAsync()
        {
            using var http = httpFactory.CreateClient("news-rss");
            using var resp = await http.GetAsync("https://somoskudasai.com/feed/");
            if (!resp.IsSuccessStatusCode) return (null, []);

            var xml  = (await resp.Content.ReadAsStringAsync()).TrimStart('﻿', '​', '\r', '\n', ' ');
            var doc  = System.Xml.Linq.XDocument.Parse(xml);
            var item = doc.Root?.Descendants("item").FirstOrDefault();
            if (item is null) return (null, []);

            var title    = item.Element("title")?.Value?.Trim() ?? "(sin título)";
            var link     = item.Element("link")?.Value?.Trim() ?? string.Empty;
            var imageUrl = item.Elements(Media + "content")
                               .Select(e => (string?)e.Attribute("url"))
                               .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u))
                        ?? item.Elements(Media + "thumbnail")
                               .Select(e => (string?)e.Attribute("url"))
                               .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));

            // Fetch article page for full body + images — using the SAME extraction as production
            // so this preview faithfully reflects what gets posted.
            string? body = null;
            var images = new List<string>();
            if (!string.IsNullOrWhiteSpace(link))
            {
                try
                {
                    using var artResp = await http.GetAsync(link);
                    if (artResp.IsSuccessStatusCode)
                    {
                        var html = await artResp.Content.ReadAsStringAsync();
                        var (ogImage, parsedBody) =
                            AnimeIndex.Scraper.Infrastructure.AnimeNewsFeedService.ParseArticleHtml(html);
                        body = parsedBody;
                        if (string.IsNullOrWhiteSpace(imageUrl) && !string.IsNullOrWhiteSpace(ogImage))
                            imageUrl = ogImage;
                        images = AnimeIndex.Scraper.Infrastructure.AnimeNewsFeedService.ExtractArticleImages(html);
                    }
                }
                catch { /* best-effort */ }
            }

            logger.LogDebug("Live fetch: {Title} ({Images} imgs)", title, images.Count);
            var newItem = new AnimeIndex.Api.Data.Entities.AnimeNewsItem
            {
                Id           = Guid.NewGuid(),
                SourceKey    = "kudasai",
                RssGuid      = link,
                Title        = title,
                Summary      = body,
                ImageUrl     = imageUrl,
                ArticleUrl   = link,
                PublishedAt  = DateTime.UtcNow,
                FetchedAt    = DateTime.UtcNow,
                IgPostStatus = "pending",
            };
            return (newItem, images);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static Series MakeSeries(string title, string slug, string? coverUrl) => new()
    {
        Id       = Guid.NewGuid(),
        Title    = title,
        Slug     = slug,
        CoverUrl = coverUrl,
        SeriesGenres = []
    };

    private static Episode MakeEpisode(Guid seriesId, int number, string? title) => new()
    {
        Id            = Guid.NewGuid(),
        SeriesId      = seriesId,
        EpisodeNumber = (short)number,
        Title         = title,
        IsPublished   = true,
        CreatedAt     = DateTime.UtcNow
    };

    // Mirrors AnimeNewsPublisherService.BuildCaption — short editorial caption from rewritten content.
    private static readonly string[] PreviewBaseHashtags =
        ["anime", "animelatino", "animenoticias", "manga", "otaku", "sheicobanime"];

    private static string BuildPreviewCaption(NewsContent c)
    {
        var sb = new StringBuilder();
        sb.Append("📰 ").Append(c.Headline).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(c.Caption)) sb.Append(c.Caption).Append("\n\n");
        sb.Append("🔔 Seguinos para más noticias de anime\n");
        sb.Append("▶️ Mirá anime gratis · Link en la bio\n\n");

        var tags = PreviewBaseHashtags.Concat(c.Hashtags)
            .Select(t => t.TrimStart('#').Replace(" ", "").ToLowerInvariant())
            .Where(t => t.Length is > 1 and < 30)
            .Distinct().Take(14).Select(t => "#" + t);
        sb.Append(string.Join(" ", tags));
        sb.Append("\n\n  [contenido: ").Append(c.FromAi ? "Gemini" : "heurística (sin key)").Append(']');
        return sb.ToString();
    }

    private static AnimeIndex.Api.Data.Entities.AnimeNewsItem MakeNewsItem(
        string title, string? summary, string? imageUrl) => new()
    {
        Id           = Guid.NewGuid(),
        SourceKey    = "test",
        RssGuid      = Guid.NewGuid().ToString(),
        Title        = title,
        Summary      = summary,
        ImageUrl     = imageUrl,
        ArticleUrl   = "https://www.animenewsnetwork.com/news/test",
        PublishedAt  = DateTime.UtcNow,
        FetchedAt    = DateTime.UtcNow,
        IgPostStatus = "pending",
    };
}
