using AnimeIndex.Api.Data;
using AnimeIndex.Api.Infrastructure.Scraping;
using AnimeIndex.Scraper.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Jobs;

/// <summary>
/// Hangfire job: picks up a single pending ScrapeJob by ID, dispatches to the
/// matching IScrapeStrategy, and updates status via DeadLetterAlerter.
/// Enqueued by ScrapeSchedulerJob or directly from the admin API.
/// </summary>
public class ScrapeOrchestratorJob(
    IEnumerable<IScrapeStrategy> strategies,
    AppDbContext db,
    DeadLetterAlerter alerter,
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
            }
            else
            {
                await alerter.HandleFailureAsync(
                    scrapeJobId, result.ErrorMessage ?? "Unknown error", ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception in scrape job {JobId}", scrapeJobId);
            await alerter.HandleFailureAsync(scrapeJobId, ex.Message, ct);
            throw; // Let Hangfire mark the job as failed and retry
        }
    }
}
