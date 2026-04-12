using AnimeIndex.Api.Data;
using AnimeIndex.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AnimeIndex.Api.Data;

public static class SeedData
{
    private static readonly string[] DefaultGenres =
    [
        "Action", "Adventure", "Comedy", "Drama", "Ecchi",
        "Fantasy", "Horror", "Isekai", "Mecha", "Mystery",
        "Psychological", "Romance", "Sci-Fi", "Slice of Life",
        "Sports", "Supernatural", "Thriller"
    ];

    public static async Task SeedAsync(AppDbContext db)
    {
        await SeedGenresAsync(db);
        await SeedTestSeriesAsync(db);
    }

    public static async Task SeedGenresAsync(AppDbContext db)
    {
        var existing = await db.Genres.Select(g => g.Name).ToListAsync();
        var toAdd = DefaultGenres.Where(g => !existing.Contains(g)).ToList();

        if (toAdd.Count == 0) return;

        foreach (var name in toAdd)
            db.Genres.Add(new Genre { Name = name });

        await db.SaveChangesAsync();
    }

    private static async Task SeedTestSeriesAsync(AppDbContext db)
    {
        if (await db.Series.AnyAsync()) return;

        var actionGenre = await db.Genres.FirstOrDefaultAsync(g => g.Name == "Action");
        var fantasyGenre = await db.Genres.FirstOrDefaultAsync(g => g.Name == "Fantasy");
        var dramaGenre = await db.Genres.FirstOrDefaultAsync(g => g.Name == "Drama");
        var adventureGenre = await db.Genres.FirstOrDefaultAsync(g => g.Name == "Adventure");

        var series1 = new Series
        {
            Slug = "sample-anime-alpha",
            Title = "Sample Anime Alpha",
            TitleRomaji = "Sampuru Anime Arufa",
            Synopsis = "A brave hero sets out on an epic quest to save the world from darkness. This is test data for development.",
            Year = 2024,
            Status = "ongoing",
            Type = "tv",
            Score = 8.5m,
            EpisodeCount = 24
        };

        var series2 = new Series
        {
            Slug = "sample-anime-beta",
            Title = "Sample Anime Beta",
            TitleRomaji = "Sampuru Anime Beta",
            Synopsis = "In a world where magic and technology collide, a young student discovers their hidden powers. Test series for development.",
            Year = 2023,
            Status = "completed",
            Type = "tv",
            Score = 7.8m,
            EpisodeCount = 12
        };

        db.Series.AddRange(series1, series2);
        await db.SaveChangesAsync();

        // Add genre associations
        if (actionGenre is not null)
        {
            db.SeriesGenres.Add(new SeriesGenre { SeriesId = series1.Id, GenreId = actionGenre.Id });
            db.SeriesGenres.Add(new SeriesGenre { SeriesId = series2.Id, GenreId = actionGenre.Id });
        }
        if (fantasyGenre is not null)
            db.SeriesGenres.Add(new SeriesGenre { SeriesId = series1.Id, GenreId = fantasyGenre.Id });
        if (adventureGenre is not null)
            db.SeriesGenres.Add(new SeriesGenre { SeriesId = series1.Id, GenreId = adventureGenre.Id });
        if (dramaGenre is not null)
            db.SeriesGenres.Add(new SeriesGenre { SeriesId = series2.Id, GenreId = dramaGenre.Id });

        await db.SaveChangesAsync();

        // Add test episodes for series1
        for (short i = 1; i <= 3; i++)
        {
            db.Episodes.Add(new Episode
            {
                SeriesId = series1.Id,
                EpisodeNumber = i,
                Title = $"Episode {i}: The Beginning Part {i}",
                IsPublished = true,
                AiredAt = new DateTime(2024, 1, i * 7, 0, 0, 0, DateTimeKind.Utc)
            });
        }

        // Add test episodes for series2
        for (short i = 1; i <= 2; i++)
        {
            db.Episodes.Add(new Episode
            {
                SeriesId = series2.Id,
                EpisodeNumber = i,
                Title = $"Chapter {i}",
                IsPublished = true,
                AiredAt = new DateTime(2023, 4, i * 7, 0, 0, 0, DateTimeKind.Utc)
            });
        }

        await db.SaveChangesAsync();
    }
}
