using AnimeIndex.Api.Data;
using AnimeIndex.Api.DTOs;
using AnimeIndex.Api.Infrastructure.Cache;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace AnimeIndex.Api.Endpoints;

public static class GenreEndpoints
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    public static void MapGenreEndpoints(this WebApplication app)
    {
        app.MapGet("/genres", GetGenres).WithTags("Genres");
    }

    private static async Task<IResult> GetGenres(
        AppDbContext db,
        ICacheService cache,
        CancellationToken ct = default)
    {
        var cacheKey = "genres:all";
        var cached = await cache.GetAsync<GenreDto[]>(cacheKey, ct);
        if (cached is not null) return Results.Ok(cached);

        var genres = await db.Genres
            .AsNoTracking()
            .OrderBy(g => g.Name)
            .ToListAsync(ct);

        var dtos = genres.Select(g => g.Adapt<GenreDto>()).ToArray();
        await cache.SetAsync(cacheKey, dtos, CacheDuration, ct);
        return Results.Ok(dtos);
    }
}
