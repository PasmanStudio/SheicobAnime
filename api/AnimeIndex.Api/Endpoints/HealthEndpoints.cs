using AnimeIndex.Api.Data;
using AnimeIndex.Api.Infrastructure.Cache;
using Microsoft.EntityFrameworkCore;

namespace AnimeIndex.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", async (AppDbContext db, ICacheService cache) =>
        {
            var dbStatus = "error";
            try
            {
                await db.Database.CanConnectAsync();
                dbStatus = "ok";
            }
            catch
            {
                // DB unavailable
            }

            var cacheStatus = await cache.PingAsync() ? "ok" : "error";

            var overallStatus = dbStatus == "ok" && cacheStatus == "ok" ? "healthy" : "degraded";

            var response = new
            {
                status = overallStatus,
                db = dbStatus,
                cache = cacheStatus,
                version = typeof(HealthEndpoints).Assembly.GetName().Version?.ToString() ?? "0.1.0"
            };

            return overallStatus == "healthy"
                ? Results.Ok(response)
                : Results.Json(response, statusCode: 503);
        });
    }
}
