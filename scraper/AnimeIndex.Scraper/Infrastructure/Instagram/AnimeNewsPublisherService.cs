using AnimeIndex.Api.Data;
using AnimeIndex.Api.Data.Entities;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>
/// Publishes pending anime news items to Instagram as:
///   • A single feed post (1080×1080)
///   • A story (1080×1920)
///
/// One item = one feed post + one story (published in sequence).
/// Errors are caught per-item so a failure on one doesn't block the rest.
/// </summary>
public class AnimeNewsPublisherService(
    AppDbContext db,
    InstagramSettings igSettings,
    MetaGraphApiClient api,
    AnimeNewsImageService imageService,
    ILogger<AnimeNewsPublisherService> logger)
{
    public async Task PublishPendingAsync(
        IReadOnlyList<AnimeNewsItem> items, CancellationToken ct = default)
    {
        if (!igSettings.IsConfigured)
        {
            logger.LogInformation("Instagram not configured — skipping news publisher");
            return;
        }
        if (items.Count == 0) return;

        logger.LogInformation("AnimeNews: publishing {Count} news item(s) to Instagram", items.Count);

        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) break;
            await PublishItemAsync(item, ct);
        }
    }

    private async Task PublishItemAsync(AnimeNewsItem item, CancellationToken ct)
    {
        try
        {
            // ── Feed post ─────────────────────────────────────────────────
            string? feedMediaId = null;
            try
            {
                var feedBytes   = await imageService.GenerateFeedAsync(item, ct);
                var feedFile    = $"news-{item.SourceKey}-{item.Id.ToString("N")[..8]}-feed.jpg";
                var feedUrl     = await api.UploadImageToImgBbAsync(feedBytes, feedFile, ct);
                var caption     = BuildCaption(item);
                var containerId = await api.CreateSingleImageContainerAsync(feedUrl, caption, ct);
                await api.WaitForContainerReadyAsync(containerId, ct);
                feedMediaId = await api.PublishContainerAsync(containerId, ct);

                logger.LogInformation(
                    "AnimeNews: published feed post for [{Source}] {Title} → {MediaId}",
                    item.SourceKey, Truncate(item.Title, 60), feedMediaId);

                // First comment with article link
                await TryPostCommentAsync(feedMediaId, $"🔗 Leer más: {item.ArticleUrl}", ct);
            }
            catch (Exception ex) when (ex is not TaskCanceledException { CancellationToken.IsCancellationRequested: true })
            {
                logger.LogWarning(ex,
                    "AnimeNews: failed to publish feed post for {Title}", Truncate(item.Title, 60));
            }

            // ── Story ─────────────────────────────────────────────────────
            string? storyMediaId = null;
            try
            {
                var storyBytes = await imageService.GenerateStoryAsync(item, ct);
                var storyFile  = $"news-{item.SourceKey}-{item.Id.ToString("N")[..8]}-story.jpg";
                var storyUrl    = await api.UploadImageToImgBbAsync(storyBytes, storyFile, ct);
                var storyContainerId = await api.CreateStoryContainerAsync(storyUrl, item.ArticleUrl, ct);
                await api.WaitForContainerReadyAsync(storyContainerId, ct);
                storyMediaId = await api.PublishContainerAsync(storyContainerId, ct);

                logger.LogInformation(
                    "AnimeNews: published story for [{Source}] {Title} → {MediaId}",
                    item.SourceKey, Truncate(item.Title, 60), storyMediaId);
            }
            catch (Exception ex) when (ex is not TaskCanceledException { CancellationToken.IsCancellationRequested: true })
            {
                logger.LogWarning(ex,
                    "AnimeNews: failed to publish story for {Title}", Truncate(item.Title, 60));
            }

            // Mark as published (even if only one of the two formats succeeded)
            var anyPublished = feedMediaId is not null || storyMediaId is not null;
            item.IgPostStatus    = anyPublished ? "published" : "failed";
            item.IgFeedMediaId   = feedMediaId;
            item.IgStoryMediaId  = storyMediaId;
            item.IgPostedAt      = anyPublished ? DateTime.UtcNow : null;
        }
        catch (Exception ex) when (ex is not TaskCanceledException { CancellationToken.IsCancellationRequested: true })
        {
            item.IgPostStatus   = "failed";
            item.ErrorMessage   = ex.Message[..Math.Min(ex.Message.Length, 500)];
            logger.LogError(ex, "AnimeNews: unexpected error publishing {Title}", Truncate(item.Title, 60));
        }
        finally
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    // ── Caption ──────────────────────────────────────────────────────────────

    // Instagram caption limit is 2200 characters.
    private const int IgCaptionMaxChars = 2200;
    private const string Hashtags = "#animelatam #animenoticias #otaku #anime #animeespañol #manga #sheicobanime";

    /// <summary>
    /// Builds the Instagram caption: title first, then article body paragraphs,
    /// then hashtags. Truncates body paragraphs so the total stays under 2200 chars.
    /// </summary>
    private static string BuildCaption(AnimeNewsItem item)
    {
        // Fixed parts (title + blank + hashtags) — these are always included.
        var header = item.Title + "\n\n";
        var footer = "\n" + Hashtags;
        var budget = IgCaptionMaxChars - header.Length - footer.Length;

        var bodyBuilder = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(item.Summary) && budget > 0)
        {
            var paragraphs = item.Summary
                .Split(["\n\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0);

            foreach (var p in paragraphs)
            {
                var chunk = p + "\n\n";
                if (bodyBuilder.Length + chunk.Length > budget)
                {
                    // Fit as many chars as possible — prefer sentence boundary, then word boundary
                    var remaining = budget - bodyBuilder.Length - 1;
                    if (remaining > 20)
                    {
                        var slice = p[..Math.Min(remaining, p.Length)].TrimEnd();
                        var sentenceEnd = slice.LastIndexOfAny(['.', '!', '?']);
                        if (sentenceEnd > 10)
                            bodyBuilder.Append(slice[..(sentenceEnd + 1)]);
                        else
                        {
                            var lastSpace = slice.LastIndexOf(' ');
                            bodyBuilder.Append((lastSpace > 0 ? slice[..lastSpace] : slice) + "…");
                        }
                    }
                    break;
                }
                bodyBuilder.Append(chunk);
            }
        }

        return header + bodyBuilder.ToString().TrimEnd() + footer;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task TryPostCommentAsync(string mediaId, string comment, CancellationToken ct)
    {
        try
        {
            await api.PostCommentAsync(mediaId, comment, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "AnimeNews: could not post comment on {MediaId}", mediaId);
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
