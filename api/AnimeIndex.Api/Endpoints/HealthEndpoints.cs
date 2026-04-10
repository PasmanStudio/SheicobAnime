using AnimeIndex.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AnimeIndex.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", async (AppDbContext db) =>
        {
            var dbStatus = "error";
            try
            {
                await db.Database.ExecuteSqlRawAsync("SELECT 1");
                dbStatus = "ok";
            }
            catch
            {
                // DB unavailable
            }

            // Cache check will be added in Phase 2 when ICacheService is created
            var cacheStatus = "not_configured";

            var overallStatus = dbStatus == "ok" ? "healthy" : "degraded";

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
