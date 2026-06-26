using AnimeIndex.Api.Data;
using AnimeIndex.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>
/// Publishes newly scraped episodes to Instagram as a single carousel post
/// (or a single-image post when there is only 1 new episode).
///
/// Strategy:
///   • Find up to MaxCarouselItems episodes published in the last 25 hours
///     that have no instagram_posts row yet.
///   • Generate one 1080×1080 image per episode (SkiaSharp).
///   • Upload all images to imgbb to get public HTTPS URLs.
///   • Create carousel item containers (one per image) via Meta Graph API.
///   • Create the carousel parent container with the combined caption.
///   • Publish the carousel.
///   • Record one instagram_posts row per episode (all share the same ig_media_id).
///
/// Fallback (1 episode):
///   Instagram carousels require ≥ 2 items, so 1 episode is posted as a
///   regular single-image feed post instead.
///
/// Called as best-effort from ScrapeOrchestratorJob — exceptions never
/// propagate to the scrape job status.
/// </summary>
public class InstagramPublisherService(
    AppDbContext db,
    InstagramSettings settings,
    MetaGraphApiClient api,
    InstagramImageService imageService,
    CaptionGeneratorService captionGen,
    ILogger<InstagramPublisherService> logger)
{
    public async Task PublishNewEpisodesAsync(CancellationToken ct = default)
    {
        if (!settings.IsConfigured)
        {
            logger.LogInformation("Instagram not configured — skipping publisher (set AccessToken, IgUserId, ImgBbApiKey)");
            return;
        }

        await CheckTokenExpiryAsync(ct);

        var since = DateTime.UtcNow.AddHours(-25);

        // ── Feed / Carousel ───────────────────────────────────────────────
        var alreadyFeedPosted = await db.InstagramPosts
            .Where(p => (p.PostType == "feed" || p.PostType == "carousel_item")
                     && (p.Status == "published" || p.Status == "skipped"))
            .Select(p => p.EpisodeId)
            .Distinct()
            .ToListAsync(ct);

        var episodes = await db.Episodes
            .Include(e => e.Series)
                .ThenInclude(s => s.SeriesGenres!)
                    .ThenInclude(sg => sg.Genre)
            .Where(e => e.IsPublished
                     && e.CreatedAt >= since
                     && !alreadyFeedPosted.Contains(e.Id))
            .OrderByDescending(e => e.CreatedAt)
            .Take(settings.MaxCarouselItems)
            .ToListAsync(ct);

        if (episodes.Count == 0)
            logger.LogInformation("No new episodes to post to Instagram today (feed)");
        else
        {
            logger.LogInformation("Preparing Instagram {PostType} with {Count} episode(s)",
                episodes.Count == 1 ? "post" : "carousel", episodes.Count);

            if (episodes.Count == 1)
                await PublishSingleAsync(episodes[0], ct);
            else
                await PublishCarouselAsync(episodes, ct);
        }

        // ── Story (one per run — most recent new episode) ─────────────────
        var alreadyStoryPosted = await db.InstagramPosts
            .Where(p => p.PostType == "story"
                     && (p.Status == "published" || p.Status == "skipped"))
            .Select(p => p.EpisodeId)
            .Distinct()
            .ToListAsync(ct);

        // Prefer the first episode from today's feed batch; fall back to a DB query
        // in case the feed was already published on a previous run.
        var storyEpisode = episodes.FirstOrDefault(e => !alreadyStoryPosted.Contains(e.Id))
            ?? await db.Episodes
                .Include(e => e.Series)
                .Where(e => e.IsPublished
                         && e.CreatedAt >= since
                         && !alreadyStoryPosted.Contains(e.Id))
                .OrderByDescending(e => e.CreatedAt)
                .FirstOrDefaultAsync(ct);

        if (storyEpisode is not null)
            await PublishStoryAsync(storyEpisode, ct);
        else
            logger.LogInformation("No new episodes to post to Instagram today (story)");
    }

    // ── Single image post (1 episode) ────────────────────────────────

    private async Task PublishSingleAsync(Episode episode, CancellationToken ct)
    {
        var record = CreateRecord(episode, "feed");
        db.InstagramPosts.Add(record);

        try
        {
            var imageBytes = await imageService.GenerateFeedAsync(episode.Series, episode, ct);
            var fileName   = BuildFileName(episode.Series.Slug, episode.EpisodeNumber, "single");
            var publicUrl  = await api.UploadImageAsync(imageBytes, fileName, ct);

            var items    = new List<(Series, Episode)> { (episode.Series, episode) };
            var caption  = captionGen.GenerateCarouselCaption(items);
            var containerId = await api.CreateSingleImageContainerAsync(publicUrl, caption, ct);
            await api.WaitForContainerReadyAsync(containerId, ct);
            var mediaId = await api.PublishContainerAsync(containerId, ct);

            record.Status      = "published";
            record.IgMediaId   = mediaId;
            record.Caption     = caption;
            record.PublishedAt = DateTime.UtcNow;

            logger.LogInformation("Published single post for {Series} ep {Ep} → {MediaId}",
                episode.Series.Title, episode.EpisodeNumber, mediaId);

            var comment = captionGen.GenerateEpisodeLinksComment([(episode.Series, episode)]);
            await PostFirstCommentAsync(mediaId, comment, ct);
        }
        catch (Exception ex)
        {
            record.Status       = "failed";
            record.ErrorMessage = ex.Message[..Math.Min(ex.Message.Length, 500)];
            logger.LogError(ex, "Failed to publish single post for episode {EpisodeId}", episode.Id);
        }
        finally
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    // ── Carousel post (2-10 episodes) ────────────────────────────────

    private async Task PublishCarouselAsync(List<Episode> episodes, CancellationToken ct)
    {
        // Pre-create DB records (all start as failed — updated on success)
        var records = episodes.Select(e => CreateRecord(e, "carousel_item")).ToList();
        db.InstagramPosts.AddRange(records);
        await db.SaveChangesAsync(ct);

        string? mediaId = null;
        try
        {
            // 1. Generate + upload images in order, wait for each item to be FINISHED
            var childContainerIds = new List<string>();
            foreach (var episode in episodes)
            {
                if (ct.IsCancellationRequested) break;
                logger.LogDebug("Generating image for {Series} ep {Ep}", episode.Series.Title, episode.EpisodeNumber);

                var imageBytes = await imageService.GenerateFeedAsync(episode.Series, episode, ct);
                var fileName   = BuildFileName(episode.Series.Slug, episode.EpisodeNumber, "carousel");
                var publicUrl  = await api.UploadImageAsync(imageBytes, fileName, ct);
                var itemId     = await api.CreateCarouselItemContainerAsync(publicUrl, ct);

                // Meta requires each item container to reach FINISHED before the parent is created
                await api.WaitForContainerReadyAsync(itemId, ct);
                childContainerIds.Add(itemId);
                logger.LogDebug("Carousel item ready: {ItemId} ({Series} ep {Ep})",
                    itemId, episode.Series.Title, episode.EpisodeNumber);
            }

            // 2. Generate combined caption
            var items   = episodes.Select(e => (e.Series, e)).ToList<(Series, Episode)>();
            var caption = captionGen.GenerateCarouselCaption(items);

            // 3. Create carousel parent container
            var carouselId = await api.CreateCarouselContainerAsync(childContainerIds, caption, ct);

            // 4. Wait for all items to process
            await api.WaitForContainerReadyAsync(carouselId, ct);

            // 5. Publish
            mediaId = await api.PublishContainerAsync(carouselId, ct);

            // 6. Mark all records as published
            foreach (var record in records)
            {
                record.Status      = "published";
                record.IgMediaId   = mediaId;
                record.Caption     = caption;
                record.PublishedAt = DateTime.UtcNow;
            }

            logger.LogInformation(
                "Published carousel with {Count} episodes → IG Media {MediaId}",
                episodes.Count, mediaId);

            var comment = captionGen.GenerateEpisodeLinksComment(items);
            await PostFirstCommentAsync(mediaId, comment, ct);
        }
        catch (Exception ex)
        {
            foreach (var record in records)
            {
                record.Status       = "failed";
                record.ErrorMessage = ex.Message[..Math.Min(ex.Message.Length, 500)];
            }
            logger.LogError(ex, "Carousel publish failed for {Count} episodes", episodes.Count);
        }
        finally
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    // ── Story post (1 episode, 1080×1920 with link sticker) ─────────────

    private async Task PublishStoryAsync(Episode episode, CancellationToken ct)
    {
        var record = CreateRecord(episode, "story");
        db.InstagramPosts.Add(record);

        try
        {
            var imageBytes = await imageService.GenerateStoryAsync(episode.Series, episode, ct);
            var fileName   = BuildFileName(episode.Series.Slug, episode.EpisodeNumber, "story");
            var publicUrl  = await api.UploadImageAsync(imageBytes, fileName, ct);

            var episodeUrl = $"{settings.SiteUrl}/series/{episode.Series.Slug}/{episode.EpisodeNumber}";
            var containerId = await api.CreateStoryContainerAsync(publicUrl, episodeUrl, ct);
            await api.WaitForContainerReadyAsync(containerId, ct);
            var mediaId = await api.PublishContainerAsync(containerId, ct);

            record.Status      = "published";
            record.IgMediaId   = mediaId;
            record.PublishedAt = DateTime.UtcNow;

            logger.LogInformation("Published story for {Series} ep {Ep} → {MediaId} (link: {Url})",
                episode.Series.Title, episode.EpisodeNumber, mediaId, episodeUrl);
        }
        catch (Exception ex)
        {
            record.Status       = "failed";
            record.ErrorMessage = ex.Message[..Math.Min(ex.Message.Length, 500)];
            logger.LogError(ex, "Failed to publish story for episode {EpisodeId}", episode.Id);
        }
        finally
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static InstagramPost CreateRecord(Episode episode, string postType) => new()
    {
        EpisodeId = episode.Id,
        PostType  = postType,
        Status    = "failed",    // overwritten on success in the finally block
        CreatedAt = DateTime.UtcNow
    };

    private static string BuildFileName(string slug, short epNumber, string tag) =>
        $"{slug}-ep{epNumber}-{tag}-{DateTime.UtcNow:yyyyMMddHHmmss}.jpg";

    private async Task PostFirstCommentAsync(string mediaId, string comment, CancellationToken ct)
    {
        try
        {
            var commentId = await api.PostCommentAsync(mediaId, comment, ct);
            logger.LogInformation("Posted episode links comment {CommentId} on media {MediaId}", commentId, mediaId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to post first comment on media {MediaId} — post remains published", mediaId);
        }
    }

    private async Task CheckTokenExpiryAsync(CancellationToken ct)
    {
        try
        {
            var days = await api.GetTokenExpiryDaysAsync(ct);
            if (days <= 0)
                logger.LogError("Instagram access token EXPIRED — posts will fail. Update INSTAGRAM_ACCESS_TOKEN immediately.");
            else if (days < 10)
                logger.LogWarning("Instagram access token expires in {Days:F1} days — renew INSTAGRAM_ACCESS_TOKEN soon", days);
            else if (days < 20)
                logger.LogInformation("Instagram access token expires in {Days:F0} days", days);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check Instagram token expiry");
        }
    }
}
