using AnimeIndex.Api.Data;
using AnimeIndex.Api.DTOs;
using AnimeIndex.Api.Infrastructure;
using AnimeIndex.Api.Infrastructure.Cache;
using AnimeIndex.Api.Infrastructure.Proxy;
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
        HttpContext httpCtx,
        Guid id,
        AppDbContext db,
        ResolverRegistry registry,
        ProxyUrlSigner proxySigner,
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
            var absoluteBase = $"{httpCtx.Request.Scheme}://{httpCtx.Request.Host}";
            var dto = ToDto(resolved, proxySigner, absoluteBase);

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
        IConfiguration config,
        CancellationToken ct = default)
    {
        var mirrors = await db.Mirrors
            .AsNoTracking()
            .Where(m => m.EpisodeId == episodeId && m.IsActive)
            .OrderBy(m => m.Priority)
            .ToListAsync(ct);

        // "Solo míos" desde una fecha: solo afecta episodios nuevos; los viejos
        // quedan intactos. Solo consultamos CreatedAt cuando el filtro está activo.
        var since = OwnHostMirrors.ParseSince(config);
        if (since is not null)
        {
            var createdAt = await db.Episodes
                .Where(e => e.Id == episodeId)
                .Select(e => (DateTime?)e.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (createdAt is not null)
                mirrors = OwnHostMirrors.Apply(mirrors, createdAt.Value, since);
        }

        var items = mirrors.Select(m => new ResolvableMirrorDto(
            MirrorId: m.Id,
            ProviderName: m.ProviderName,
            QualityLabel: m.QualityLabel,
            Priority: m.Priority,
            Resolvable: registry.Supports(m.ProviderName))).ToList();

        return Results.Ok(items);
    }

    private static ResolvedSourceDto ToDto(ResolvedSource r, ProxyUrlSigner proxySigner, string absoluteBase)
    {
        // Any source whose upstream requires a Referer MUST be proxied — browsers
        // cannot set Referer for cross-origin <video>/fetch, and the upstream will 403.
        var referer = r.Headers is not null && r.Headers.TryGetValue("Referer", out var ref0) ? ref0 : null;
        var needsProxy = r.ProxyRequired || !string.IsNullOrEmpty(referer);

        string RewriteUrl(string url) =>
            needsProxy ? proxySigner.BuildProxyPath(url, referer, absoluteBase) : url;

        return new ResolvedSourceDto(
            Url: RewriteUrl(r.Url),
            Format: r.Format.ToString().ToLowerInvariant(),
            // Headers are satisfied server-side by the proxy; frontend must not try to set them
            Headers: needsProxy ? null : r.Headers,
            Subtitles: r.Subtitles?.Select(s => new SubtitleDto(s.Language, s.Label, s.Url)).ToList(),
            Qualities: r.Qualities?.Select(q => new QualityDto(q.Height, RewriteUrl(q.Url), q.BandwidthKbps)).ToList(),
            ExpiresAt: r.ExpiresAt,
            ProxyRequired: needsProxy,
            Hoster: r.Hoster);
    }
}
