using AnimeIndex.Api.Data;
using AnimeIndex.Api.DTOs;
using AnimeIndex.Api.Infrastructure.Cache;
using AnimeIndex.Api.Infrastructure.Resolvers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AnimeIndex.Api.Endpoints;

public static class MirrorEndpoints
{
    private const short FailureThreshold = 3;

    public static void MapMirrorEndpoints(this WebApplication app)
    {
        app.MapPatch("/mirrors/{id:guid}/report", ReportMirrorFailure).WithTags("Mirrors");
        app.MapPost("/mirrors/{id:guid}/resolve", ResolveMirror).WithTags("Mirrors");
        app.MapGet("/mirrors/{episodeId:guid}/resolvable-set", GetResolvableSet).WithTags("Mirrors");
    }

    private static async Task<IResult> ReportMirrorFailure(
        AppDbContext db,
        ICacheService cache,
        Guid id,
        CancellationToken ct = default)
    {
        var mirror = await db.Mirrors.FirstOrDefaultAsync(m => m.Id == id, ct);

        if (mirror is null)
            return Results.Json(
                new ErrorResponse("Mirror not found", "NOT_FOUND"),
                statusCode: 404);

        mirror.ConsecutiveFailures++;
        mirror.LastCheckedAt = DateTime.UtcNow;

        if (mirror.ConsecutiveFailures >= FailureThreshold)
            mirror.IsActive = false;

        await db.SaveChangesAsync(ct);

        // Invalidate cached mirrors for the episode
        await cache.RemoveAsync($"episode:{mirror.EpisodeId}:mirrors", ct);

        return Results.NoContent();
    }

    /// <summary>
    /// Resolves a mirror's third-party embed URL into a fresh playable HLS/MP4 source.
    /// Result is cached in-memory only (NOT in Redis) with TTL &lt; ExpiresAt to avoid storing
    /// copyrighted manifests in shared infrastructure.
    /// </summary>
    private static async Task<IResult> ResolveMirror(
        Guid id,
        AppDbContext db,
        ResolverRegistry registry,
        IMemoryCache memCache,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var logger = loggerFactory.CreateLogger("MirrorEndpoints.Resolve");
        var cacheKey = $"resolve:{id}";

        if (memCache.TryGetValue<ResolvedSourceDto>(cacheKey, out var cached) && cached is not null)
            return Results.Ok(cached);

        var mirror = await db.Mirrors
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id, ct);

        if (mirror is null)
            return Results.Json(new ErrorResponse("Mirror not found", "NOT_FOUND"), statusCode: 404);

        if (!mirror.IsActive)
            return Results.Json(new ErrorResponse("Mirror inactive", "INACTIVE"), statusCode: 410);

        // Honour blocked_slugs for the parent series (DMCA compliance)
        var seriesIsBlocked = await db.Mirrors
            .Where(m => m.Id == id)
            .Join(db.Episodes, m => m.EpisodeId, e => e.Id, (m, e) => e.SeriesId)
            .Join(db.Series, sid => sid, s => s.Id, (sid, s) => s.Slug)
            .Join(db.BlockedSlugs, slug => slug, b => b.Slug, (slug, b) => b.Slug)
            .AnyAsync(ct);

        if (seriesIsBlocked)
            return Results.Json(new ErrorResponse("Source removed", "BLOCKED"), statusCode: 410);

        var resolver = registry.GetFor(mirror);
        if (resolver is null)
            return Results.Json(
                new ErrorResponse("This mirror provider is not supported by Sheicob player", "UNSUPPORTED"),
                statusCode: 501);

        try
        {
            var resolved = await resolver.ResolveAsync(mirror, ct);
            var dto = ToDto(resolved);

            // TTL = ExpiresAt - 5min margin, capped between 30s and 30min
            var ttl = resolved.ExpiresAt - DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
            if (ttl < TimeSpan.FromSeconds(30)) ttl = TimeSpan.FromSeconds(30);
            if (ttl > TimeSpan.FromMinutes(30)) ttl = TimeSpan.FromMinutes(30);
            memCache.Set(cacheKey, dto, ttl);

            return Results.Ok(dto);
        }
        catch (ResolverException ex)
        {
            logger.LogWarning(ex,
                "Resolver {Hoster} failed for mirror {MirrorId}: {Reason}",
                ex.Hoster, id, ex.Reason);
            return Results.Json(
                new ErrorResponse(ex.Message, ex.Reason.ToString().ToUpperInvariant()),
                statusCode: 503);
        }
    }

    /// <summary>
    /// Returns the resolvability flag for every active mirror of an episode.
    /// Frontend uses this to render resolvable mirrors as "Sheicob" buttons (badge,
    /// dorado, ordered first) WITHOUT triggering the actual extraction yet.
    /// </summary>
    private static async Task<IResult> GetResolvableSet(
        Guid episodeId,
        AppDbContext db,
        ResolverRegistry registry,
        CancellationToken ct = default)
    {
        var mirrors = await db.Mirrors
            .AsNoTracking()
            .Where(m => m.EpisodeId == episodeId && m.IsActive)
            .OrderBy(m => m.Priority)
            .ToListAsync(ct);

        var items = mirrors.Select(m => new ResolvableMirrorDto(
            MirrorId: m.Id,
            ProviderName: m.ProviderName,
            QualityLabel: m.QualityLabel,
            Priority: m.Priority,
            Resolvable: registry.Supports(m.ProviderName))).ToList();

        return Results.Ok(items);
    }

    private static ResolvedSourceDto ToDto(ResolvedSource r) => new(
        Url: r.Url,
        Format: r.Format.ToString().ToLowerInvariant(),
        Headers: r.Headers,
        Subtitles: r.Subtitles?.Select(s => new SubtitleDto(s.Language, s.Label, s.Url)).ToList(),
        Qualities: r.Qualities?.Select(q => new QualityDto(q.Height, q.Url, q.BandwidthKbps)).ToList(),
        ExpiresAt: r.ExpiresAt,
        ProxyRequired: r.ProxyRequired,
        Hoster: r.Hoster);
}
