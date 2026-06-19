using AnimeIndex.Api.Data;
using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Api.DTOs;
using AnimeIndex.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AnimeIndex.Api.Endpoints;

/// <summary>
/// Native per-episode star rating (1–5). Keyed by device id (like watch progress), so it
/// works for anonymous and logged-in viewers and the vote actually counts on our side —
/// unlike the IMDb deep-link, which can only send the user to imdb.com to rate manually.
/// </summary>
public static class EpisodeRatingEndpoints
{
    public static void MapEpisodeRatingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/episodes/{episodeId:guid}/rating").WithTags("EpisodeRating");
        group.MapGet("", GetRating);
        group.MapPost("", SubmitRating);
    }

    private static async Task<IResult> GetRating(
        Guid episodeId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var deviceId = http.GetDeviceId();
        return Results.Ok(await AggregateAsync(db, episodeId, deviceId, ct));
    }

    private static async Task<IResult> SubmitRating(
        Guid episodeId, RateEpisodeRequest request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (request.Rating is < 1 or > 5)
            return Results.Json(new ErrorResponse("Rating must be between 1 and 5", "INVALID"), statusCode: 400);

        var exists = await db.Episodes.AnyAsync(e => e.Id == episodeId, ct);
        if (!exists)
            return Results.Json(new ErrorResponse("Episode not found", "NOT_FOUND"), statusCode: 404);

        var deviceId = http.GetDeviceId();
        var existing = await db.EpisodeRatings
            .FirstOrDefaultAsync(r => r.DeviceId == deviceId && r.EpisodeId == episodeId, ct);

        if (existing is null)
        {
            db.EpisodeRatings.Add(new EpisodeRating
            {
                DeviceId = deviceId,
                EpisodeId = episodeId,
                Rating = (short)request.Rating,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Rating = (short)request.Rating;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(await AggregateAsync(db, episodeId, deviceId, ct));
    }

    private static async Task<EpisodeRatingStatsDto> AggregateAsync(
        AppDbContext db, Guid episodeId, Guid deviceId, CancellationToken ct)
    {
        var all = db.EpisodeRatings.AsNoTracking().Where(r => r.EpisodeId == episodeId);

        var count = await all.CountAsync(ct);
        var average = count == 0 ? 0 : Math.Round(await all.AverageAsync(r => (double)r.Rating, ct), 2);
        var mine = await all
            .Where(r => r.DeviceId == deviceId)
            .Select(r => (short?)r.Rating)
            .FirstOrDefaultAsync(ct);

        return new EpisodeRatingStatsDto(average, count, mine);
    }
}
