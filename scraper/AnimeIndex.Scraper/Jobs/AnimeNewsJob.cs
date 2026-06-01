using AnimeIndex.Scraper.Infrastructure;
using AnimeIndex.Scraper.Infrastructure.Instagram;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Jobs;

/// <summary>
/// Hangfire job that:
///   1. Fetches all configured RSS feeds and upserts new anime news items.
///   2. Publishes pending items to Instagram (feed post + story per item, up to MaxPerRun).
///
/// Runs twice daily (configured via Hangfire:NewsJobCron — default: 10:00 and 20:00 UTC).
/// Safe to run manually from the Hangfire dashboard for ad-hoc posting.
/// </summary>
public class AnimeNewsJob(
    AnimeNewsFeedService feedService,
    AnimeNewsPublisherService publisher,
    AnimeNewsSettings settings,
    ILogger<AnimeNewsJob> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        if (!settings.IsEnabled)
        {
            logger.LogInformation("AnimeNewsJob: no feeds configured — skipping");
            return;
        }

        logger.LogInformation("AnimeNewsJob: starting (MaxPerRun={Max}, MaxAgeHours={Age})",
            settings.MaxPerRun, settings.MaxAgeHours);

        // Step 1: fetch RSS feeds and upsert new items
        var newItems = await feedService.FetchAndUpsertAsync(ct);
        logger.LogInformation("AnimeNewsJob: {Count} new items fetched from feeds", newItems.Count);

        // Step 2: get pending items (includes both newly fetched and any from previous runs that failed)
        var pendingItems = await feedService.GetPendingItemsAsync(ct);

        if (pendingItems.Count == 0)
        {
            logger.LogInformation("AnimeNewsJob: no pending items to publish");
            return;
        }

        // Step 3: publish to Instagram
        await publisher.PublishPendingAsync(pendingItems, ct);

        logger.LogInformation("AnimeNewsJob: done — published {Count} item(s)", pendingItems.Count);
    }
}
