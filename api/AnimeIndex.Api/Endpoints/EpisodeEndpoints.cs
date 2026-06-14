using AnimeIndex.Api.Data;
using AnimeIndex.Api.DTOs;
using AnimeIndex.Api.Infrastructure;
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

        group.MapGet("/recent", GetRecentEpisodes);
        group.MapGet("/{id:guid}", GetEpisode);
        group.MapGet("/{id:guid}/mirrors", GetEpisodeMirrors);
    }

    private static async Task<IResult> GetRecentEpisodes(
        AppDbContext db,
        ICacheService cache,
        int? days,
        int? pageSize,
        CancellationToken ct = default)
    {
        var actualDays = Math.Clamp(days ?? 3, 1, 7);
        var actualPageSize = Math.Clamp(pageSize ?? 50, 1, 100);
        var since = DateTime.UtcNow.AddDays(-actualDays);

        var cacheKey = $"episodes:recent:{actualDays}:{actualPageSize}";
        var cached = await cache.GetAsync<EpisodeDto[]>(cacheKey, ct);
        if (cached is not null) return Results.Ok(cached);

        var episodes = await db.Episodes
            .Include(e => e.Series)
            .AsNoTracking()
            .Where(e => e.IsPublished && e.CreatedAt >= since)
            .OrderByDescending(e => e.CreatedAt)
            .Take(actualPageSize)
            .ToListAsync(ct);

        var dtos = episodes.Select(e => e.Adapt<EpisodeDto>()).ToArray();
        await cache.SetAsync(cacheKey, dtos, CacheDuration, ct);
        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetEpisode(
        AppDbContext db,
        ICacheService cache,
        IConfiguration config,
        Guid id,
        CancellationToken ct = default)
    {
        var ownOnly = config.GetValue("Mirrors:OwnHostsOnly", false);
        var cacheKey = $"episode:{id}:{(ownOnly ? "own" : "all")}";
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

        episode.Mirrors = OwnHostMirrors.Apply([.. episode.Mirrors], ownOnly);

        var dto = episode.Adapt<EpisodeDto>();
        await cache.SetAsync(cacheKey, dto, CacheDuration, ct);
        return Results.Ok(dto);
    }

    private static async Task<IResult> GetEpisodeMirrors(
        AppDbContext db,
        ICacheService cache,
        IConfiguration config,
        Guid id,
        CancellationToken ct = default)
    {
        var ownOnly = config.GetValue("Mirrors:OwnHostsOnly", false);
        var cacheKey = $"episode:{id}:mirrors:{(ownOnly ? "own" : "all")}";
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

        mirrors = OwnHostMirrors.Apply(mirrors, ownOnly);

        var dtos = mirrors.Select(m => m.Adapt<MirrorDto>()).ToArray();
        await cache.SetAsync(cacheKey, dtos, CacheDuration, ct);
        return Results.Ok(dtos);
    }
}
