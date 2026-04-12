using AnimeIndex.Api.Data;
using AnimeIndex.Api.DTOs;
using AnimeIndex.Api.Infrastructure.Cache;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace AnimeIndex.Api.Endpoints;

public static class SeriesEndpoints
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public static void MapSeriesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/series").WithTags("Series");

        group.MapGet("/", GetSeriesList);
        group.MapGet("/search", SearchSeries);
        group.MapGet("/{slug}", GetSeriesBySlug);
        group.MapGet("/{slug}/episodes", GetSeriesEpisodes);
    }

    private static async Task<IResult> GetSeriesList(
        AppDbContext db,
        ICacheService cache,
        int page = 1,
        int pageSize = DefaultPageSize,
        string? status = null,
        string? type = null,
        string? genre = null,
        int? year = null,
        string sort = "updated",
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var cacheKey = $"series:list:{page}:{pageSize}:{status}:{type}:{genre}:{year}:{sort}";
        var cached = await cache.GetAsync<PaginatedResponse<SeriesDto>>(cacheKey, ct);
        if (cached is not null) return Results.Ok(cached);

        var query = db.Series
            .Include(s => s.SeriesGenres).ThenInclude(sg => sg.Genre)
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(s => s.Status == status);
        if (!string.IsNullOrEmpty(type))
            query = query.Where(s => s.Type == type);
        if (year.HasValue)
            query = query.Where(s => s.Year == year.Value);
        if (!string.IsNullOrEmpty(genre))
            query = query.Where(s => s.SeriesGenres.Any(sg => sg.Genre.Name == genre));

        query = sort switch
        {
            "score" => query.OrderByDescending(s => s.Score).ThenBy(s => s.Title),
            "year" => query.OrderByDescending(s => s.Year).ThenByDescending(s => s.UpdatedAt),
            _ => query.OrderByDescending(s => s.UpdatedAt)
        };

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var response = new PaginatedResponse<SeriesDto>(
            items.Select(s => s.Adapt<SeriesDto>()).ToArray(),
            total, page, pageSize);

        await cache.SetAsync(cacheKey, response, CacheDuration, ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> SearchSeries(
        AppDbContext db,
        ICacheService cache,
        string? q,
        int page = 1,
        int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Results.Ok(new PaginatedResponse<SeriesDto>([], 0, 1, pageSize));

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var cacheKey = $"search:{q.ToLowerInvariant().Trim()}:{page}:{pageSize}";
        var cached = await cache.GetAsync<PaginatedResponse<SeriesDto>>(cacheKey, ct);
        if (cached is not null) return Results.Ok(cached);

        var searchTerm = q.Trim();

        var query = db.Series
            .Include(s => s.SeriesGenres).ThenInclude(sg => sg.Genre)
            .AsNoTracking()
            .Where(s =>
                EF.Functions.ILike(s.Title, $"%{searchTerm}%") ||
                (s.TitleRomaji != null && EF.Functions.ILike(s.TitleRomaji, $"%{searchTerm}%")) ||
                (s.TitleNative != null && EF.Functions.ILike(s.TitleNative, $"%{searchTerm}%")))
            .OrderByDescending(s => s.Score);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var response = new PaginatedResponse<SeriesDto>(
            items.Select(s => s.Adapt<SeriesDto>()).ToArray(),
            total, page, pageSize);

        await cache.SetAsync(cacheKey, response, TimeSpan.FromMinutes(2), ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetSeriesBySlug(
        AppDbContext db,
        ICacheService cache,
        string slug,
        CancellationToken ct = default)
    {
        var cacheKey = $"series:{slug}";
        var cached = await cache.GetAsync<SeriesDto>(cacheKey, ct);
        if (cached is not null) return Results.Ok(cached);

        var series = await db.Series
            .Include(s => s.SeriesGenres).ThenInclude(sg => sg.Genre)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Slug == slug, ct);

        if (series is null)
            return Results.Json(
                new ErrorResponse("Series not found", "NOT_FOUND"),
                statusCode: 404);

        var dto = series.Adapt<SeriesDto>();
        await cache.SetAsync(cacheKey, dto, CacheDuration, ct);
        return Results.Ok(dto);
    }

    private static async Task<IResult> GetSeriesEpisodes(
        AppDbContext db,
        ICacheService cache,
        string slug,
        int page = 1,
        int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var series = await db.Series
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Slug == slug, ct);

        if (series is null)
            return Results.Json(
                new ErrorResponse("Series not found", "NOT_FOUND"),
                statusCode: 404);

        var cacheKey = $"series:{series.Id}:episodes:{page}:{pageSize}";
        var cached = await cache.GetAsync<PaginatedResponse<EpisodeDto>>(cacheKey, ct);
        if (cached is not null) return Results.Ok(cached);

        var query = db.Episodes
            .AsNoTracking()
            .Where(e => e.SeriesId == series.Id)
            .OrderBy(e => e.EpisodeNumber);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var response = new PaginatedResponse<EpisodeDto>(
            items.Select(e => e.Adapt<EpisodeDto>()).ToArray(),
            total, page, pageSize);

        await cache.SetAsync(cacheKey, response, CacheDuration, ct);
        return Results.Ok(response);
    }
}
