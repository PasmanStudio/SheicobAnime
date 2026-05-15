using AnimeIndex.Api.Data;
using AnimeIndex.Api.Infrastructure.Scraping;
using AnimeIndex.Scraper.Infrastructure;
using AnimeIndex.Scraper.Infrastructure.Instagram;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Jobs;

/// <summary>
/// Hangfire job: picks up a single pending ScrapeJob by ID, dispatches to the
/// matching IScrapeStrategy, and updates status via DeadLetterAlerter.
/// Enqueued by ScrapeSchedulerJob or directly from the admin API.
/// Retries disabled — scrape jobs are long-running; the ScrapeSchedulerJob
/// handles recovery by detecting stuck/failed jobs and re-enqueuing.
/// After completion (success or failure), stops the host so the GHA step
/// exits immediately instead of waiting for the 6-hour timeout.
/// On success, runs InstagramPublisherService as a best-effort post-step
/// (failure never propagates back to the scrape job status).
/// </summary>
[AutomaticRetry(Attempts = 0)]
public class ScrapeOrchestratorJob(
    IEnumerable<IScrapeStrategy> strategies,
    AppDbContext db,
    DeadLetterAlerter alerter,
    InstagramPublisherService instagramPublisher,
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    ILogger<ScrapeOrchestratorJob> logger)
{
    public async Task ExecuteAsync(Guid scrapeJobId, string sourceKey, CancellationToken ct = default)
    {
        var strategy = strategies.FirstOrDefault(s => s.SourceKey == sourceKey);
        if (strategy is null)
        {
            logger.LogError("No IScrapeStrategy registered for sourceKey={SourceKey}", sourceKey);
            await alerter.HandleFailureAsync(
                scrapeJobId, $"No strategy found for sourceKey '{sourceKey}'", ct);
            return;
        }

        // Mark as running
        var job = await db.ScrapeJobs.FindAsync([scrapeJobId], ct);
        if (job is not null)
        {
            job.Status = "running";
            job.LastHeartbeat = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Executing scrape job {JobId} with strategy {SourceKey}", scrapeJobId, sourceKey);

        try
        {
            var result = await strategy.ScrapeAsync(scrapeJobId, ct);

            if (result.Success)
            {
                await alerter.HandleSuccessAsync(scrapeJobId, ct);
                logger.LogInformation(
                    "Job {JobId} succeeded — series={S} episodes={E} mirrors={M}",
                    scrapeJobId, result.SeriesIndexed, result.EpisodesIndexed, result.MirrorsIndexed);

                // Best-effort Instagram publishing — never fails the scrape job
                try
                {
                    await instagramPublisher.PublishNewEpisodesAsync(ct);
                }
                catch (Exception igEx)
                {
                    logger.LogError(igEx, "Instagram publishing encountered an unhandled error (non-critical)");
                }
            }
            else
            {
                await alerter.HandleFailureAsync(
                    scrapeJobId, result.ErrorMessage ?? "Unknown error", ct);
            }

            // Signal the host to stop: the GHA runner only needs one scrape cycle.
            // Without this the Hangfire server runs until the 6-hour GHA timeout.
            logger.LogInformation("Job {JobId} finished — requesting host shutdown", scrapeJobId);
            lifetime.StopApplication();
        }
        catch (OperationCanceledException)
        {
            // Process is shutting down (GHA timeout / SIGTERM) OR an internal
            // timeout fired. Mark as "pending" so the scheduler re-enqueues it
            // and resumes Phase 2 from DB instead of restarting Phase 1.
            //
            // Use a FRESH scope — the injected `db` may already be disposed
            // by the time SIGTERM tears down the DI scope.
            logger.LogWarning(
                "Job {JobId} cancelled — marking pending for resume",
                scrapeJobId);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var freshDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var cancelled = await freshDb.ScrapeJobs.FindAsync([scrapeJobId]);
                if (cancelled is not null)
                {
                    cancelled.Status = "pending";
                    cancelled.ErrorMessage = "Paused by cancellation — will resume on next run";
                    await freshDb.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception dbEx)
            {
                logger.LogError(dbEx, "Failed to mark job {JobId} as pending after cancellation", scrapeJobId);
            }
            // Do NOT re-throw — Hangfire sees success; DB record is "pending" for next pickup.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception in scrape job {JobId}", scrapeJobId);
            await alerter.HandleFailureAsync(scrapeJobId, ex.Message, ct);
            throw; // Let Hangfire mark the job as failed and log it
        }
    }
}
