using AnimeIndex.Api.Data;
using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Api.DTOs;
using AnimeIndex.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AnimeIndex.Api.Endpoints;

public static class ProgressEndpoints
{
    /// <summary>Min position a viewer must reach before we persist progress (reduces junk rows).</summary>
    private const int MinTrackedSeconds = 10;

    /// <summary>Viewer is considered to have completed the episode once past this ratio.</summary>
    private const double CompletedRatio = 0.90;

    public static void MapProgressEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/progress").WithTags("WatchProgress");
        group.MapPut("/{episodeId:guid}", UpdateProgress);
        group.MapGet("/{episodeId:guid}", GetProgress);
        group.MapGet("/recent", GetRecent);
    }

    private static async Task<IResult> UpdateProgress(
        Guid episodeId,
        UpdateProgressRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        if (request.PositionSeconds < 0 || request.DurationSeconds <= 0)
            return Results.Json(new ErrorResponse("Invalid position/duration", "INVALID"), statusCode: 400);

        // Don't persist trivially short progress — avoid spamming the table
        if (request.PositionSeconds < MinTrackedSeconds)
            return Results.NoContent();

        var episode = await db.Episodes
            .AsNoTracking()
            .Where(e => e.Id == episodeId)
            .Select(e => new { e.Id, e.Series!.Slug })
            .FirstOrDefaultAsync(ct);

        if (episode is null)
            return Results.Json(new ErrorResponse("Episode not found", "NOT_FOUND"), statusCode: 404);

        var deviceId = http.GetDeviceId();
        var completed = request.DurationSeconds > 0 &&
            (double)request.PositionSeconds / request.DurationSeconds >= CompletedRatio;

        // Upsert via EF — ON CONFLICT DO UPDATE
        var existing = await db.WatchProgress
            .FirstOrDefaultAsync(w => w.DeviceId == deviceId && w.EpisodeId == episodeId, ct);

        if (existing is null)
        {
            db.WatchProgress.Add(new WatchProgress
            {
                DeviceId = deviceId,
                EpisodeId = episodeId,
                SeriesSlug = episode.Slug,
                PositionSeconds = request.PositionSeconds,
                DurationSeconds = request.DurationSeconds,
                Completed = completed,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.PositionSeconds = request.PositionSeconds;
            existing.DurationSeconds = request.DurationSeconds;
            existing.SeriesSlug = episode.Slug;
            // Once completed, stay completed — but allow time backfill for seeking within
            existing.Completed = existing.Completed || completed;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetProgress(
        Guid episodeId,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var deviceId = http.GetDeviceId();
        var row = await db.WatchProgress
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.DeviceId == deviceId && w.EpisodeId == episodeId, ct);

        if (row is null)
            return Results.Json(new ErrorResponse("No progress", "NOT_FOUND"), statusCode: 404);

        return Results.Ok(new WatchProgressDto(
            row.EpisodeId.ToString(),
            row.SeriesSlug,
            row.PositionSeconds,
            row.DurationSeconds,
            row.Completed,
            row.UpdatedAt));
    }

    private static async Task<IResult> GetRecent(
        HttpContext http,
        AppDbContext db,
        CancellationToken ct,
        int limit = 20)
    {
        if (limit <= 0 || limit > 50) limit = 20;

        var deviceId = http.GetDeviceId();

        var rows = await (
            from w in db.WatchProgress.AsNoTracking()
            where w.DeviceId == deviceId && !w.Completed
            orderby w.UpdatedAt descending
            join e in db.Episodes on w.EpisodeId equals e.Id
            join s in db.Series on e.SeriesId equals s.Id
            select new RecentProgressDto(
                w.EpisodeId.ToString(),
                w.SeriesSlug,
                s.Title,
                s.CoverUrl,
                e.EpisodeNumber,
                e.Title,
                w.PositionSeconds,
                w.DurationSeconds,
                w.Completed,
                w.UpdatedAt))
            .Take(limit)
            .ToListAsync(ct);

        return Results.Ok(rows);
    }
}
