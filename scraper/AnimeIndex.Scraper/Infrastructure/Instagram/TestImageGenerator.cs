using AnimeIndex.Api.Data.Entities;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

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

    public static async Task RunAsync(IHttpClientFactory httpFactory, ILoggerFactory loggerFactory)
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

        // ── News images ───────────────────────────────────────────────────────
        Console.WriteLine($"\n═══ NEWS IMAGES ({NewsSamples.Length * 2} files) ═══");
        var newsLogger  = loggerFactory.CreateLogger<AnimeNewsImageService>();
        var newsService = new AnimeNewsImageService(httpFactory, newsLogger);

        foreach (var (slug, title, summary, imageUrl) in NewsSamples)
        {
            var newsItem = MakeNewsItem(title, summary, imageUrl);

            var sw = Stopwatch.StartNew();
            var feedBytes = await newsService.GenerateFeedAsync(newsItem);
            sw.Stop();
            var feedPath = Path.Combine(outDir, $"news-{slug}-feed.jpg");
            await File.WriteAllBytesAsync(feedPath, feedBytes);
            Console.WriteLine($"  [feed ] {title[..Math.Min(title.Length, 60)]}  →  {feedBytes.Length / 1024} KB  ({sw.ElapsedMilliseconds} ms)");

            sw.Restart();
            var storyBytes = await newsService.GenerateStoryAsync(newsItem);
            sw.Stop();
            var storyPath = Path.Combine(outDir, $"news-{slug}-story.jpg");
            await File.WriteAllBytesAsync(storyPath, storyBytes);
            Console.WriteLine($"  [story] {title[..Math.Min(title.Length, 60)]}  →  {storyBytes.Length / 1024} KB  ({sw.ElapsedMilliseconds} ms)");

            // Show the caption that would be posted (first sample only)
            if (slug == "kudasai-cloverworks" && !string.IsNullOrWhiteSpace(summary))
            {
                Console.WriteLine("\n  ── Caption preview ──────────────────────────");
                var caption = BuildSampleCaption(newsItem);
                foreach (var line in caption.Split('\n'))
                    Console.WriteLine($"  {line}");
                Console.WriteLine("  ─────────────────────────────────────────────\n");
            }
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

    private static string BuildSampleCaption(AnimeIndex.Api.Data.Entities.AnimeNewsItem item)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Summary))
        {
            var paragraphs = item.Summary
                .Split(["\n\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (paragraphs.Count > 0)
            {
                lines.Add($"@sheicobanime 👉 {paragraphs[0]}");
                lines.Add(string.Empty);
                for (var i = 1; i < paragraphs.Count; i++) { lines.Add($"📌 {paragraphs[i]}"); lines.Add(string.Empty); }
            }
        }
        else { lines.Add($"@sheicobanime 👉 {item.Title}"); lines.Add(string.Empty); }
        lines.Add("#animelatam #animenoticias #otaku #anime #animeespañol #manga #sheicobanime");
        return string.Join("\n", lines);
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
