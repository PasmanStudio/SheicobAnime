using AnimeIndex.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Jobs;

/// <summary>
/// Recurring job: purge watch_progress rows older than retention window.
/// Keeps the table from growing unbounded for anonymous (cookie-based) viewers.
/// </summary>
public class WatchProgressCleanupJob(AppDbContext db, ILogger<WatchProgressCleanupJob> logger)
{
    private const int RetentionDays = 180;

    public async Task RunAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        var deleted = await db.WatchProgress
            .Where(wp => wp.UpdatedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
        {
            logger.LogInformation(
                "WatchProgressCleanupJob: deleted {Count} rows older than {Cutoff:o}",
                deleted, cutoff);
        }
    }
}
