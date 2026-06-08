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

            // Liveness is determined by the database ONLY. Redis is a soft dependency
            // (the cache degrades to Postgres), so a down/over-quota Redis must NOT make
            // this endpoint return 503 — otherwise Render's health check fails and it
            // restarts the instance in a loop while the site is actually serving fine.
            var dbHealthy = dbStatus == "ok";
            var overallStatus = dbHealthy
                ? (cacheStatus == "ok" ? "healthy" : "degraded")
                : "unhealthy";

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

            // 200 as long as the DB is reachable (even when cache is degraded);
            // 503 only when the database itself is down.
            return dbHealthy
                ? Results.Ok(response)
                : Results.Json(response, statusCode: 503);
        });
    }
}
