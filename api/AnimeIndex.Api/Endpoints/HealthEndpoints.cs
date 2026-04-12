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
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var dbStatus = "error";
            long dbMs = 0;
            try
            {
                var dbSw = System.Diagnostics.Stopwatch.StartNew();
                await db.Database.CanConnectAsync();
                dbMs = dbSw.ElapsedMilliseconds;
                dbStatus = "ok";
            }
            catch
            {
                // DB unavailable
            }

            var cacheSw = System.Diagnostics.Stopwatch.StartNew();
            var cacheOk = await cache.PingAsync();
            var cacheMs = cacheSw.ElapsedMilliseconds;
            var cacheStatus = cacheOk ? "ok" : "error";

            var overallStatus = dbStatus == "ok" && cacheStatus == "ok" ? "healthy" : "degraded";

            var response = new
            {
                status = overallStatus,
                db = dbStatus,
                dbMs,
                cache = cacheStatus,
                cacheMs,
                totalMs = sw.ElapsedMilliseconds,
                version = typeof(HealthEndpoints).Assembly.GetName().Version?.ToString() ?? "0.1.0"
            };

            return overallStatus == "healthy"
                ? Results.Ok(response)
                : Results.Json(response, statusCode: 503);
        });
    }
}
