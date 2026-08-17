using AnimeIndex.Api.Data;
using AnimeIndex.Api.Infrastructure.Cache;
using Microsoft.EntityFrameworkCore;

namespace AnimeIndex.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        // ─── /health = LIVENESS PURO (lo que mira Render) ────────────────────
        // NO toca la DB a propósito. Antes hacía CanConnectAsync y devolvía 503
        // si fallaba: bajo ráfaga de crawlers el pool de Npgsql se saturaba, el
        // health check timeouteaba, Render marcaba la instancia como no sana y
        // la REINICIABA — arranque en frío, más presión sobre la DB, y otra vez.
        // Ese bucle es lo que producía los mails "Application exited early".
        // Un 503 acá solo debe significar "el proceso está muerto"; si la DB se
        // cae, el API sigue vivo sirviendo desde cache y se recupera solo.
        app.MapGet("/health", () => Results.Ok(new
        {
            status = "alive",
            version = typeof(HealthEndpoints).Assembly.GetName().Version?.ToString() ?? "0.1.0"
        }));

        // ─── /health/ready = diagnóstico de dependencias (manual/monitoreo) ──
        // Reporta el estado real de DB y cache. NO lo usa el health check de
        // Render — es para mirar a mano o desde un monitor externo.
        app.MapGet("/health/ready", async (AppDbContext db, ICacheService cache, CancellationToken ct) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var dbStatus = "error";
            long dbMs = 0;
            try
            {
                // Timeout propio y corto: si la DB está saturada este endpoint
                // debe responder rápido "error", no quedarse colgado ocupando
                // una conexión del pool que necesitan los requests reales.
                using var dbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dbCts.CancelAfter(TimeSpan.FromSeconds(5));

                var dbSw = System.Diagnostics.Stopwatch.StartNew();
                await db.Database.CanConnectAsync(dbCts.Token);
                dbMs = dbSw.ElapsedMilliseconds;
                dbStatus = "ok";
            }
            catch
            {
                // DB unavailable / saturada
            }

            var cacheSw = System.Diagnostics.Stopwatch.StartNew();
            var cacheOk = await cache.PingAsync();
            var cacheMs = cacheSw.ElapsedMilliseconds;
            var cacheStatus = cacheOk ? "ok" : "error";

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

            return dbHealthy
                ? Results.Ok(response)
                : Results.Json(response, statusCode: 503);
        });
    }
}
