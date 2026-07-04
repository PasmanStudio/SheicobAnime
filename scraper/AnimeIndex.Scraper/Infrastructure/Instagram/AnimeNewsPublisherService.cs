using System.Text;
using AnimeIndex.Api.Data;
using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Scraper.Infrastructure;
using AnimeIndex.Scraper.Infrastructure.AiRewrite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>
/// Publishes pending anime news items to Instagram as:
///   • A single feed post (1080×1080)
///   • A story (1080×1920)
///   • Máx. UN Reel de noticias por día (motion card animada + música por IA,
///     share_to_feed) — el primer item publicable del día lo gana; dedup por
///     ig_reel_media_id en las últimas 24 h.
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
    InstagramVideoService videoService,
    ReelMusicService musicService,
    NewsRewriteService rewriter,
    IHttpClientFactory httpFactory,
    ILogger<AnimeNewsPublisherService> logger)
{
    // Meta procesa video asíncrono y puede tardar varios minutos
    private static readonly TimeSpan VideoProcessingTimeout = TimeSpan.FromMinutes(6);

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
            // Gather all usable article images (cover + in-body) for an image-rich carousel.
            var images = await GatherImagesAsync(item, ct);

            // Guarantee every post has a real image — never publish a text-only/flat poster.
            // Checked BEFORE the rewrite so we don't spend a Gemini call on an unpostable item.
            if (!await imageService.HasDecodableImageAsync(images, ct))
            {
                item.IgPostStatus = "skipped";
                item.ErrorMessage = "No decodable image";
                logger.LogWarning("AnimeNews: skipping \"{Title}\" — no usable image could be downloaded",
                    Truncate(item.Title, 60));
                return;
            }

            // Turn the raw item into finished, original content (AI rewrite, or clean fallback).
            var content = await rewriter.RewriteAsync(item, ct);

            // ── Feed (carousel: cover + content slides + closing CTA) ──
            string? feedMediaId = null;
            try
            {
                var slides  = await imageService.GenerateCarouselSlidesAsync(
                    item, content, images, newsSettings.MaxContentSlides, ct);
                var caption = BuildCaption(content);

                if (slides.Count == 1)
                {
                    // No body text → single-image post (carousels require ≥ 2 items)
                    var url         = await api.UploadImageAsync(slides[0], SlideFileName(item, 0), ct);
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
                        var url    = await api.UploadImageAsync(slides[i], SlideFileName(item, i), ct);
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
                var storyBytes = await imageService.GenerateStoryAsync(item, content, images, ct);
                var storyFile  = $"news-{item.SourceKey}-{item.Id.ToString("N")[..8]}-story.jpg";
                var storyUrl    = await api.UploadImageAsync(storyBytes, storyFile, ct);
                // Link sticker points to OUR site (not the source article) — drives traffic to us.
                var storyContainerId = await api.CreateStoryContainerAsync(storyUrl, igSettings.SiteUrl, ct);
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

            // ── Reel diario (máx 1 por 24 h — el primer item publicable lo gana) ──
            string? reelMediaId = null;
            if (igSettings.NewsReelEnabled)
            {
                try
                {
                    var reelRecently = await db.AnimeNewsItems.AnyAsync(
                        n => n.IgReelMediaId != null && n.IgPostedAt >= DateTime.UtcNow.AddHours(-24), ct);
                    if (!reelRecently)
                        reelMediaId = await PublishReelAsync(item, content, images, ct);
                }
                catch (Exception ex) when (ex is not TaskCanceledException { CancellationToken.IsCancellationRequested: true })
                {
                    logger.LogWarning(ex, "AnimeNews: reel dedup check failed — skipping reel this run");
                }
            }

            // Mark as published (even if only one of the formats succeeded)
            var anyPublished = feedMediaId is not null || storyMediaId is not null || reelMediaId is not null;
            item.IgPostStatus    = anyPublished ? "published" : "failed";
            item.IgFeedMediaId   = feedMediaId;
            item.IgStoryMediaId  = storyMediaId;
            item.IgReelMediaId   = reelMediaId;
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

    // ── Reel de noticias (motion card + música por IA) ───────────────────────

    /// <summary>
    /// Publishes the animated news Reel: fondo con Ken Burns + titular entrando
    /// con slide/fade + track según el mood de la noticia. Best-effort — un
    /// fallo acá nunca afecta el feed/story ya publicados.
    /// </summary>
    private async Task<string?> PublishReelAsync(
        AnimeNewsItem item, NewsContent content, IReadOnlyList<string> images, CancellationToken ct)
    {
        try
        {
            var (background, overlay) = await imageService.GenerateStoryLayersAsync(item, content, images, ct);
            var music = await musicService.SelectAndDownloadForNewsAsync(
                content.Headline, content.Lede, item.RssGuid, ct);

            var videoBytes = await videoService.GenerateMotionCardAsync(
                background, overlay, music?.Mp3, music?.Track.StartSeconds ?? 0, ct);

            var fileName = $"news-{item.SourceKey}-{item.Id.ToString("N")[..8]}-reel.mp4";
            var videoUrl = await api.UploadVideoAsync(videoBytes, fileName, ct);

            var caption = BuildCaption(content);
            if (music?.Track.Attribution is { } attribution)
            {
                // Que la atribución nunca empuje el caption sobre el límite de IG
                var budget = IgCaptionMaxChars - attribution.Length - 2;
                if (caption.Length > budget) caption = caption[..budget].TrimEnd();
                caption = $"{caption}\n\n{attribution}";
            }

            var containerId = await api.CreateReelContainerAsync(videoUrl, caption, shareToFeed: true, ct);
            await api.WaitForContainerReadyAsync(containerId, ct, VideoProcessingTimeout);
            var mediaId = await api.PublishContainerAsync(containerId, ct);

            logger.LogInformation("AnimeNews: published REEL for [{Source}] {Title} → {MediaId}",
                item.SourceKey, Truncate(item.Title, 60), mediaId);
            return mediaId;
        }
        catch (FfmpegNotAvailableException ex)
        {
            logger.LogWarning("AnimeNews: reel skipped — {Reason}", ex.Message);
            return null;
        }
        catch (Exception ex) when (ex is not TaskCanceledException { CancellationToken.IsCancellationRequested: true })
        {
            logger.LogWarning(ex, "AnimeNews: failed to publish reel for {Title}", Truncate(item.Title, 60));
            return null;
        }
    }

    // ── Caption ──────────────────────────────────────────────────────────────

    // Instagram caption limit is 2200 characters.
    private const int IgCaptionMaxChars = 2200;

    // Always-on base hashtags. No "ñ" (Instagram mangles "#animeespañol") and no spaces.
    private static readonly string[] BaseHashtags =
        ["anime", "animelatino", "animenoticias", "manga", "otaku", "sheicobanime"];

    /// <summary>
    /// Builds the Instagram caption from the already-rewritten content: a headline line, the
    /// original editorial body (the rewrite — never the source text), a CTA, smart hashtags
    /// and the handle. Short by design, like koryugi/kamiread — no wall of copied text.
    /// </summary>
    private string BuildCaption(NewsContent content)
    {
        var sb = new StringBuilder();
        sb.Append("📰 ").Append(content.Headline.Trim()).Append("\n\n");

        if (!string.IsNullOrWhiteSpace(content.Caption))
            sb.Append(content.Caption.Trim()).Append("\n\n");

        sb.Append("🔔 Seguinos para más noticias de anime\n");
        sb.Append("▶️ Mirá anime gratis · Link en la bio\n\n");
        sb.Append(BuildHashtags(content.Hashtags));
        if (!string.IsNullOrWhiteSpace(igSettings.Handle))
            sb.Append("\n\n@").Append(igSettings.Handle);

        var result = sb.ToString();
        return result.Length <= IgCaptionMaxChars ? result : result[..IgCaptionMaxChars].TrimEnd();
    }

    /// <summary>Merges the base hashtags with the AI's topic hashtags (deduped, sanitized).</summary>
    private static string BuildHashtags(IReadOnlyList<string> aiTags)
    {
        var tags = BaseHashtags.Concat(aiTags)
            .Select(t => t.TrimStart('#').Replace(" ", "").Replace("#", "").ToLowerInvariant())
            .Where(t => t.Length is > 1 and < 30)
            .Distinct()
            .Take(14)
            .Select(t => "#" + t);
        return string.Join(" ", tags);
    }

    // ── Image gathering ─────────────────────────────────────────────────────────

    /// <summary>
    /// Collects every usable image for the carousel: the stored cover plus any in-body images
    /// from the article page (best-effort re-fetch). More images ⇒ a richer, koryugi-style carousel.
    /// </summary>
    private async Task<IReadOnlyList<string>> GatherImagesAsync(AnimeNewsItem item, CancellationToken ct)
    {
        var images = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.ImageUrl)) images.Add(item.ImageUrl!);
        if (string.IsNullOrWhiteSpace(item.ArticleUrl)) return images;

        try
        {
            var http = httpFactory.CreateClient("news-rss");
            using var resp = await http.GetAsync(item.ArticleUrl, ct);
            if (resp.IsSuccessStatusCode)
            {
                var html = await resp.Content.ReadAsStringAsync(ct);
                images.AddRange(AnimeNewsFeedService.ExtractArticleImages(html));
            }
        }
        catch (Exception ex) when (ex is not TaskCanceledException { CancellationToken.IsCancellationRequested: true })
        {
            logger.LogDebug(ex, "AnimeNews: could not gather extra images for {Title}", Truncate(item.Title, 50));
        }

        return images.Distinct().Take(6).ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string SlideFileName(AnimeNewsItem item, int index) =>
        $"news-{item.SourceKey}-{item.Id.ToString("N")[..8]}-{index}.jpg";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
