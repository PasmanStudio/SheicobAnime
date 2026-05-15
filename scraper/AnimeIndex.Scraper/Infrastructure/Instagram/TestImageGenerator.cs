using AnimeIndex.Api.Data.Entities;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>
/// Generates sample Story and Feed images to disk for visual review.
/// Invoked via: dotnet run --project scraper/AnimeIndex.Scraper -- --images
/// Output: ./test-output/instagram/  (relative to working directory)
/// </summary>
public static class TestImageGenerator
{
    private static readonly (string Title, string Slug, string? CoverUrl, int Episode, string? EpTitle)[] Samples =
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

    public static async Task RunAsync(IHttpClientFactory httpFactory, ILoggerFactory loggerFactory)
    {
        var outDir = Path.Combine(Directory.GetCurrentDirectory(), "test-output", "instagram");
        Directory.CreateDirectory(outDir);

        var logger  = loggerFactory.CreateLogger<InstagramImageService>();
        var service = new InstagramImageService(httpFactory, logger);
        var total   = Stopwatch.StartNew();

        Console.WriteLine($"\nGenerating {Samples.Length * 2} images → {outDir}\n");

        foreach (var (title, slug, coverUrl, ep, epTitle) in Samples)
        {
            var series  = MakeSeries(title, slug, coverUrl);
            var episode = MakeEpisode(series.Id, ep, epTitle);

            // Feed (1080×1080)
            var sw = Stopwatch.StartNew();
            var feedBytes = await service.GenerateFeedAsync(series, episode);
            sw.Stop();
            var feedPath = Path.Combine(outDir, $"{slug}-ep{ep}-feed.jpg");
            await File.WriteAllBytesAsync(feedPath, feedBytes);
            Console.WriteLine($"  [feed ] {title} ep{ep}  →  {feedBytes.Length / 1024} KB  ({sw.ElapsedMilliseconds} ms)");

            // Story (1080×1920)
            sw.Restart();
            var storyBytes = await service.GenerateStoryAsync(series, episode);
            sw.Stop();
            var storyPath = Path.Combine(outDir, $"{slug}-ep{ep}-story.jpg");
            await File.WriteAllBytesAsync(storyPath, storyBytes);
            Console.WriteLine($"  [story] {title} ep{ep}  →  {storyBytes.Length / 1024} KB  ({sw.ElapsedMilliseconds} ms)");
        }

        total.Stop();
        Console.WriteLine($"\nDone in {total.ElapsedMilliseconds} ms total.");
        Console.WriteLine($"Open: {outDir}\n");
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
}
