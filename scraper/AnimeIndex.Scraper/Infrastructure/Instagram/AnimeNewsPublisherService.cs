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
    AnimeNewsSettings newsSettings,
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
            // ── Feed (carousel: cover + content slides; single image if no body) ──
            string? feedMediaId = null;
            try
            {
                var slides  = await imageService.GenerateCarouselSlidesAsync(
                    item, newsSettings.MaxContentSlides, ct);
                var caption = BuildCaption(item);

                if (slides.Count == 1)
                {
                    // No body text → single-image post (carousels require ≥ 2 items)
                    var url         = await api.UploadImageToImgBbAsync(slides[0], SlideFileName(item, 0), ct);
                    var containerId = await api.CreateSingleImageContainerAsync(url, caption, ct);
                    await api.WaitForContainerReadyAsync(containerId, ct);
                    feedMediaId = await api.PublishContainerAsync(containerId, ct);
                }
                else
                {
                    // Upload each slide, create a child container, wait for FINISHED, then carousel
                    var childIds = new List<string>(slides.Count);
                    for (var i = 0; i < slides.Count; i++)
                    {
                        var url    = await api.UploadImageToImgBbAsync(slides[i], SlideFileName(item, i), ct);
                        var itemId = await api.CreateCarouselItemContainerAsync(url, ct);
                        await api.WaitForContainerReadyAsync(itemId, ct);
                        childIds.Add(itemId);
                    }
                    var carouselId = await api.CreateCarouselContainerAsync(childIds, caption, ct);
                    await api.WaitForContainerReadyAsync(carouselId, ct);
                    feedMediaId = await api.PublishContainerAsync(carouselId, ct);
                }

                logger.LogInformation(
                    "AnimeNews: published {Kind} for [{Source}] {Title} → {MediaId}",
                    slides.Count == 1 ? "post" : $"carousel ({slides.Count} slides)",
                    item.SourceKey, Truncate(item.Title, 60), feedMediaId);
            }
            catch (Exception ex) when (ex is not TaskCanceledException { CancellationToken.IsCancellationRequested: true })
            {
                logger.LogWarning(ex,
                    "AnimeNews: failed to publish feed for {Title}", Truncate(item.Title, 60));
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

    // Alternating leading emojis for body paragraphs — easy-to-scan, editorial style.
    private static readonly string[] BodyEmojis = ["📌", "🎬", "✨", "🔥", "💬", "🎌"];

    /// <summary>
    /// Builds the Instagram caption. The slides are punchy posters, so the caption carries
    /// the actual story: title + body paragraphs (with alternating emojis) + CTA + hashtags
    /// + handle. The body is fitted to the 2200-char limit at a sentence boundary, so it is
    /// never cut mid-word (Instagram itself adds the "… más" expander when displaying).
    /// </summary>
    private string BuildCaption(AnimeNewsItem item)
    {
        var header = "📰 " + item.Title + "\n\n";

        var footer = new System.Text.StringBuilder();
        footer.Append("\n👉 Seguí leyendo · Link en bio\n\n").Append(Hashtags);
        if (!string.IsNullOrWhiteSpace(igSettings.Handle))
            footer.Append("\n\n@").Append(igSettings.Handle);
        var footerStr = footer.ToString();

        var budget = IgCaptionMaxChars - header.Length - footerStr.Length;
        return header + BuildBody(item.Summary, budget) + footerStr;
    }

    /// <summary>
    /// Renders body paragraphs with alternating leading emojis, fitting within
    /// <paramref name="budget"/> characters. If the body overflows, the last paragraph is
    /// cut at a sentence boundary (then a word boundary) — never mid-word.
    /// </summary>
    private static string BuildBody(string? summary, int budget)
    {
        if (string.IsNullOrWhiteSpace(summary) || budget <= 0) return string.Empty;

        var paragraphs = summary
            .Split(["\n\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var sb  = new System.Text.StringBuilder();
        var idx = 0;
        foreach (var p in paragraphs)
        {
            var prefix = BodyEmojis[idx % BodyEmojis.Length] + " ";
            var chunk  = prefix + p + "\n\n";

            if (sb.Length + chunk.Length > budget)
            {
                var remaining = budget - sb.Length - prefix.Length - 1;
                if (remaining > 40)
                {
                    var slice = p[..Math.Min(remaining, p.Length)].TrimEnd();
                    var sentenceEnd = slice.LastIndexOfAny(['.', '!', '?']);
                    if (sentenceEnd > 30)
                        sb.Append(prefix).Append(slice[..(sentenceEnd + 1)]);
                    else
                    {
                        var lastSpace = slice.LastIndexOf(' ');
                        sb.Append(prefix).Append(lastSpace > 0 ? slice[..lastSpace] : slice).Append('…');
                    }
                }
                break;
            }

            sb.Append(chunk);
            idx++;
        }

        return sb.ToString().TrimEnd();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string SlideFileName(AnimeNewsItem item, int index) =>
        $"news-{item.SourceKey}-{item.Id.ToString("N")[..8]}-{index}.jpg";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
