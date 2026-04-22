using AnimeIndex.Api.Data;
using AnimeIndex.Api.Infrastructure.Scraping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Jobs;

/// <summary>
/// Recurring job that probes existing mirrors to verify they are still embeddable.
/// Deactivates dead mirrors and increments consecutive_failures.
/// Runs daily — processes up to 500 mirrors per run (oldest-checked first).
/// </summary>
public class MirrorHealthCheckJob(
    AppDbContext db,
    MirrorProbeService probe,
    ILogger<MirrorHealthCheckJob> logger)
{
    private const int BatchSize = 500;
    private const short DeactivateThreshold = 3;

    public async Task RunAsync(CancellationToken ct = default)
    {
        // Pick active mirrors that haven't been checked recently (or ever)
        var mirrors = await db.Mirrors
            .Where(m => m.IsActive)
            .OrderBy(m => m.LastCheckedAt ?? DateTime.MinValue)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (mirrors.Count == 0)
        {
            logger.LogDebug("MirrorHealthCheck: no mirrors to check");
            return;
        }

        var deactivated = 0;
        var stillAlive = 0;

        foreach (var mirror in mirrors)
        {
            if (ct.IsCancellationRequested) break;

            var embeddable = await probe.IsEmbeddableAsync(mirror.EmbedUrl, ct);
            mirror.LastCheckedAt = DateTime.UtcNow;

            if (embeddable)
            {
                // Reset failure count on success
                mirror.ConsecutiveFailures = 0;
                stillAlive++;
            }
            else
            {
                mirror.ConsecutiveFailures++;

                if (mirror.ConsecutiveFailures >= DeactivateThreshold)
                {
                    mirror.IsActive = false;
                    deactivated++;
                    logger.LogDebug(
                        "Deactivated mirror {MirrorId} ({Provider}) after {Failures} consecutive failures",
                        mirror.Id, mirror.ProviderName, mirror.ConsecutiveFailures);
                }
            }
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "MirrorHealthCheck complete — checked={Checked}, alive={Alive}, deactivated={Deactivated}",
            mirrors.Count, stillAlive, deactivated);
    }
}
