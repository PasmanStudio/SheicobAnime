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
    MetaGraphApiClient api,
    AnimeNewsSettings settings,
    ILogger<AnimeNewsJob> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            await RunCoreAsync(ct);
        }
        finally
        {
            // Los assets de Cloudinary son de un solo uso: Meta ya los copió a su
            // CDN, y si la publicación falló no se reintentan entre corridas. Va en
            // finally para que las corridas que fallan —las que MÁS basura dejan—
            // también limpien. Best-effort: PurgeUploadedAsync no propaga.
            await api.PurgeUploadedAsync(ct);
        }
    }

    private async Task RunCoreAsync(CancellationToken ct)
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

        // Step 2: get pending candidates. Se pide un pool más grande que MaxPerRun:
        // cuando el Reel del día está disponible, el publisher elige la noticia
        // MÁS RELEVANTE del pool (IA/heurística) en vez de la más nueva, y el
        // resto queda pendiente para las corridas siguientes.
        var candidates = await feedService.GetPendingItemsAsync(ct, take: Math.Max(settings.MaxPerRun, 10));

        if (candidates.Count == 0)
        {
            logger.LogInformation("AnimeNewsJob: no pending items to publish");
            return;
        }

        // Step 3: publish to Instagram (el publisher capa el batch a MaxPerRun)
        await publisher.PublishPendingAsync(candidates, ct);

        logger.LogInformation("AnimeNewsJob: done");
    }
}
