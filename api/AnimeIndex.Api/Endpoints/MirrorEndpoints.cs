using AnimeIndex.Api.Data;
using AnimeIndex.Api.DTOs;
using AnimeIndex.Api.Infrastructure.Cache;
using Microsoft.EntityFrameworkCore;

namespace AnimeIndex.Api.Endpoints;

public static class MirrorEndpoints
{
    private const short FailureThreshold = 3;

    public static void MapMirrorEndpoints(this WebApplication app)
    {
        app.MapPatch("/mirrors/{id:guid}/report", ReportMirrorFailure).WithTags("Mirrors");
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
}
