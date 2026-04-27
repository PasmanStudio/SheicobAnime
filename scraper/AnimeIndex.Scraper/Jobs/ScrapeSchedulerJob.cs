using AnimeIndex.Api.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AnimeIndex.Scraper.Jobs;

/// <summary>
/// Hangfire recurring job: scans the scrape_jobs table for pending records
/// and enqueues individual ScrapeOrchestratorJob instances for each one.
/// Backfill jobs (JobType "backfill:*") are routed to BackfillJob.
/// Registered in the scraper worker's Program.cs with a configurable cron.
/// </summary>
public class ScrapeSchedulerJob(
    AppDbContext db,
    IBackgroundJobClient jobClient,
    ILogger<ScrapeSchedulerJob> logger)
{
    // Sources that should always have a recurring scrape cycle
    private static readonly string[] _autoSources = ["source2"];

    // Minimum hours between auto-created scrape cycles per source
    private const int AutoRescheduleHours = 24;

    public async Task RunAsync(CancellationToken ct = default)
    {
        // ── Auto-reschedule: if a source has no pending/running job and its
        //    last completed job is older than AutoRescheduleHours, create one.
        var now = DateTime.UtcNow;
        foreach (var source in _autoSources)
        {
            var jobType = $"scrape:{source}";
            var hasPendingOrRunning = await db.ScrapeJobs
                .AnyAsync(j => j.JobType == jobType &&
                               (j.Status == "pending" || j.Status == "running"), ct);

            if (!hasPendingOrRunning)
            {
                var lastCompleted = await db.ScrapeJobs
                    .Where(j => j.JobType == jobType && j.Status == "completed")
                    .MaxAsync(j => (DateTime?)j.ScheduledAt, ct);

                var hoursSinceLast = lastCompleted.HasValue
                    ? (now - lastCompleted.Value).TotalHours
                    : AutoRescheduleHours + 1; // no prior completion → schedule now

                if (hoursSinceLast >= AutoRescheduleHours)
                {
                    db.ScrapeJobs.Add(new AnimeIndex.Api.Data.Entities.ScrapeJob
                    {
                        JobType = jobType,
                        Status = "pending",
                        ScheduledAt = now,
                    });
                    logger.LogInformation(
                        "ScrapeSchedulerJob: auto-enqueued new scrape cycle for {Source} " +
                        "(last completed {Hours:F1}h ago)", source, hoursSinceLast);
                }
            }
        }

        await db.SaveChangesAsync(ct);

        // ── Stuck-job recovery: reset "running" jobs that haven't sent a
        //    heartbeat in over 6 hours — they may be processing a very long series
        //    (hundreds of episodes) without being truly dead.
        const int StuckHeartbeatTimeoutMinutes = 360;
        var heartbeatCutoff = now.AddMinutes(-StuckHeartbeatTimeoutMinutes);
        var stuckJobs = await db.ScrapeJobs
            .Where(j => j.Status == "running" &&
                        (j.LastHeartbeat != null ? j.LastHeartbeat <= heartbeatCutoff
                                                 : j.ScheduledAt <= heartbeatCutoff))
            .ToListAsync(ct);

        foreach (var stuck in stuckJobs)
        {
            stuck.Status = "failed";
            stuck.ErrorMessage = $"No heartbeat for {StuckHeartbeatTimeoutMinutes} min — reset by scheduler";
            stuck.CompletedAt = now;
            logger.LogWarning(
                "ScrapeSchedulerJob: reset stuck job {JobId} ({JobType}) — last heartbeat {Heartbeat}",
                stuck.Id, stuck.JobType, stuck.LastHeartbeat?.ToString("o") ?? "never");
        }

        if (stuckJobs.Count > 0)
            await db.SaveChangesAsync(ct);

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
            if (job.JobType.StartsWith("backfill:"))
            {
                // Route backfill jobs to BackfillJob with maxPages from metadata
                var maxPages = ParseMaxPages(job.ErrorMessage);
                jobClient.Enqueue<BackfillJob>(
                    j => j.ExecuteAsync(job.Id, maxPages, CancellationToken.None));
            }
            else
            {
                // Default to source1 if no explicit source is encoded in JobType
                var sourceKey = job.JobType.StartsWith("scrape:")
                    ? job.JobType["scrape:".Length..]
                    : "source1";

                jobClient.Enqueue<ScrapeOrchestratorJob>(
                    j => j.ExecuteAsync(job.Id, sourceKey, CancellationToken.None));
            }

            // Mark as queued so scheduler doesn't re-pick it up on next tick
            job.Status = "running";
        }

        await db.SaveChangesAsync(ct);
    }

    private static int ParseMaxPages(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 200;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("maxPages", out var mp) ? mp.GetInt32() : 200;
        }
        catch { return 200; }
    }
}
