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
        group.MapGet("/sitemap", GetEpisodeSitemap);
        group.MapGet("/{id:guid}", GetEpisode);
        group.MapGet("/{id:guid}/mirrors", GetEpisodeMirrors);
    }

    // Sitemap de episodios: TODO el catálogo publicado, paginado y liviano
    // (slug + número + fecha). /recent no sirve para esto — clampa a 7 días.
    // Cache 1h: solo lo piden los crawlers vía /sitemap-episodes/{n}.xml del web.
    private static async Task<IResult> GetEpisodeSitemap(
        AppDbContext db,
        ICacheService cache,
        int? page,
        int? pageSize,
        CancellationToken ct = default)
    {
        var actualPage = Math.Max(page ?? 1, 1);
        var actualPageSize = Math.Clamp(pageSize ?? 10_000, 1, 10_000);

        var cacheKey = $"episodes:sitemap:{actualPage}:{actualPageSize}";
        var cached = await cache.GetAsync<PaginatedResponse<EpisodeSitemapDto>>(cacheKey, ct);
        if (cached is not null) return Results.Ok(cached);

        var query = db.Episodes
            .AsNoTracking()
            .Where(e => e.IsPublished);

        var total = await query.CountAsync(ct);
        var items = await query
            // Orden estable entre páginas: los episodios nuevos se agregan al final,
            // así los chunks ya indexados no cambian de contenido.
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .Skip((actualPage - 1) * actualPageSize)
            .Take(actualPageSize)
            .Select(e => new EpisodeSitemapDto(e.Series.Slug, e.EpisodeNumber, e.CreatedAt))
            .ToListAsync(ct);

        var response = new PaginatedResponse<EpisodeSitemapDto>(
            [.. items], total, actualPage, actualPageSize);
        await cache.SetAsync(cacheKey, response, TimeSpan.FromHours(1), ct);
        return Results.Ok(response);
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
        var since = OwnHostMirrors.ParseSince(config);
        var cacheKey = $"episode:{id}:{(since is null ? "all" : "new")}";
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

        episode.Mirrors = OwnHostMirrors.Apply([.. episode.Mirrors], episode.CreatedAt, since);

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
        var since = OwnHostMirrors.ParseSince(config);
        var cacheKey = $"episode:{id}:mirrors:{(since is null ? "all" : "new")}";
        var cached = await cache.GetAsync<MirrorDto[]>(cacheKey, ct);
        if (cached is not null) return Results.Ok(cached);

        // CreatedAt en vez de un simple existence-check: lo necesita el filtro por fecha.
        var createdAt = await db.Episodes
            .Where(e => e.Id == id)
            .Select(e => (DateTime?)e.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (createdAt is null)
            return Results.Json(
                new ErrorResponse("Episode not found", "NOT_FOUND"),
                statusCode: 404);

        var mirrors = await db.Mirrors
            .AsNoTracking()
            .Where(m => m.EpisodeId == id && m.IsActive)
            .OrderBy(m => m.Priority)
            .ToListAsync(ct);

        mirrors = OwnHostMirrors.Apply(mirrors, createdAt.Value, since);

        var dtos = mirrors.Select(m => m.Adapt<MirrorDto>()).ToArray();
        await cache.SetAsync(cacheKey, dtos, CacheDuration, ct);
        return Results.Ok(dtos);
    }
}
