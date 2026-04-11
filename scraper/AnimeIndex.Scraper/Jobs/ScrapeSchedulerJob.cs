using AnimeIndex.Api.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Jobs;

/// <summary>
/// Hangfire recurring job: scans the scrape_jobs table for pending records
/// and enqueues individual ScrapeOrchestratorJob instances for each one.
/// Registered in the scraper worker's Program.cs with a configurable cron.
/// </summary>
public class ScrapeSchedulerJob(
    AppDbContext db,
    IBackgroundJobClient jobClient,
    ILogger<ScrapeSchedulerJob> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var pending = await db.ScrapeJobs
            .Where(j => j.Status == "pending" && j.ScheduledAt <= DateTime.UtcNow)
            .OrderBy(j => j.ScheduledAt)
            .Take(20) // process at most 20 per tick to avoid overload
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            logger.LogDebug("ScrapeSchedulerJob: no pending jobs");
            return;
        }

        logger.LogInformation("ScrapeSchedulerJob: enqueuing {Count} jobs", pending.Count);

        foreach (var job in pending)
        {
            // Default to source1 if no explicit source is encoded in JobType
            var sourceKey = job.JobType.StartsWith("scrape:")
                ? job.JobType["scrape:".Length..]
                : "source1";

            jobClient.Enqueue<ScrapeOrchestratorJob>(
                j => j.ExecuteAsync(job.Id, sourceKey, CancellationToken.None));

            // Mark as queued so scheduler doesn't re-pick it up on next tick
            job.Status = "running";
        }

        await db.SaveChangesAsync(ct);
    }
}
