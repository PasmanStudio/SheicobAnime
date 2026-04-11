using AnimeIndex.Api.Data;
using AnimeIndex.Api.DTOs;
using AnimeIndex.Api.Infrastructure.Cache;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace AnimeIndex.Api.Endpoints;

public static class EpisodeEndpoints
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public static void MapEpisodeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/episodes").WithTags("Episodes");

        group.MapGet("/{id:guid}", GetEpisode);
        group.MapGet("/{id:guid}/mirrors", GetEpisodeMirrors);
    }

    private static async Task<IResult> GetEpisode(
        AppDbContext db,
        ICacheService cache,
        Guid id,
        CancellationToken ct = default)
    {
        var cacheKey = $"episode:{id}";
        var cached = await cache.GetAsync<EpisodeDto>(cacheKey, ct);
        if (cached is not null) return Results.Ok(cached);

        var episode = await db.Episodes
            .Include(e => e.Series)
            .Include(e => e.Mirrors.Where(m => m.IsActive))
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct);

        if (episode is null)
            return Results.Json(
                new ErrorResponse("Episode not found", "NOT_FOUND"),
                statusCode: 404);

        var dto = episode.Adapt<EpisodeDto>();
        await cache.SetAsync(cacheKey, dto, CacheDuration, ct);
        return Results.Ok(dto);
    }

    private static async Task<IResult> GetEpisodeMirrors(
        AppDbContext db,
        ICacheService cache,
        Guid id,
        CancellationToken ct = default)
    {
        var cacheKey = $"episode:{id}:mirrors";
        var cached = await cache.GetAsync<MirrorDto[]>(cacheKey, ct);
        if (cached is not null) return Results.Ok(cached);

        var episodeExists = await db.Episodes.AnyAsync(e => e.Id == id, ct);
        if (!episodeExists)
            return Results.Json(
                new ErrorResponse("Episode not found", "NOT_FOUND"),
                statusCode: 404);

        var mirrors = await db.Mirrors
            .AsNoTracking()
            .Where(m => m.EpisodeId == id && m.IsActive)
            .OrderBy(m => m.Priority)
            .ToListAsync(ct);

        var dtos = mirrors.Select(m => m.Adapt<MirrorDto>()).ToArray();
        await cache.SetAsync(cacheKey, dtos, CacheDuration, ct);
        return Results.Ok(dtos);
    }
}
