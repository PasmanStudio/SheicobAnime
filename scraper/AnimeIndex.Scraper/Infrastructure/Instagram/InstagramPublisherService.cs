using AnimeIndex.Api.Data;
using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Scraper.Infrastructure.AiRewrite;
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
/// Reel + story de video (jul 2026):
///   Además del feed, cada corrida publica UN Reel (motion card de 12 s
///   generada con ffmpeg — zoom Ken Burns sobre la tarjeta de story) del
///   episodio más nuevo, con share_to_feed. La story reusa el mismo MP4
///   (video story + link sticker); sin ffmpeg degrada a story de imagen.
///
/// Called as best-effort from ScrapeOrchestratorJob — exceptions never
/// propagate to the scrape job status.
/// </summary>
public class InstagramPublisherService(
    AppDbContext db,
    InstagramSettings settings,
    MetaGraphApiClient api,
    InstagramImageService imageService,
    InstagramVideoService videoService,
    ReelMusicService musicService,
    CaptionGeneratorService captionGen,
    GeminiClient gemini,
    AiSettings aiSettings,
    ILogger<InstagramPublisherService> logger)
{
    // Meta procesa video asíncrono y puede tardar varios minutos
    private static readonly TimeSpan VideoProcessingTimeout = TimeSpan.FromMinutes(6);

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

        // El "héroe" del batch (IA/heurística) encabeza todo: primera slide del
        // carrusel (= portada), story y reel. Antes era simplemente el episodio
        // más nuevo — un estreno grande (ep. 1 de una franquicia top) quedaba
        // enterrado en la slide 5 detrás de lo último que scrapeó.
        episodes = await OrderByHeroAsync(episodes, ct);

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

        // ── Reel (one per run — most recent new episode) ──────────────────
        // Motion card animada con ffmpeg. share_to_feed=true la muestra también
        // en el feed — los Reels alcanzan gente que NO sigue la cuenta.
        Guid? reelEpisodeId = null;
        string? reelVideoUrl = null;
        if (settings.ReelsEnabled)
        {
            var alreadyReelPosted = await db.InstagramPosts
                .Where(p => p.PostType == "reel"
                         && (p.Status == "published" || p.Status == "skipped"))
                .Select(p => p.EpisodeId)
                .Distinct()
                .ToListAsync(ct);

            var reelEpisode = episodes.FirstOrDefault(e => !alreadyReelPosted.Contains(e.Id))
                ?? await db.Episodes
                    .Include(e => e.Series)
                    .Where(e => e.IsPublished
                             && e.CreatedAt >= since
                             && !alreadyReelPosted.Contains(e.Id))
                    .OrderByDescending(e => e.CreatedAt)
                    .FirstOrDefaultAsync(ct);

            if (reelEpisode is not null)
            {
                reelVideoUrl = await PublishReelAsync(reelEpisode, ct);
                if (reelVideoUrl is not null) reelEpisodeId = reelEpisode.Id;
            }
            else
                logger.LogInformation("No new episodes to post to Instagram today (reel)");
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
        {
            // Si el reel de este mismo episodio ya subió su MP4, la story lo reusa
            // (video story con link sticker) — un solo render y un solo upload.
            var storyVideoUrl = storyEpisode.Id == reelEpisodeId ? reelVideoUrl : null;
            await PublishStoryAsync(storyEpisode, storyVideoUrl, ct);
        }
        else
            logger.LogInformation("No new episodes to post to Instagram today (story)");
    }

    // ── Héroe del batch (el episodio que merece la portada) ──────────

    /// <summary>
    /// Reordena el batch para que el episodio MÁS promocionable vaya primero
    /// (portada del carrusel + story + reel). Gemini elige como editor; sin
    /// API key o ante error, heurística: estrenos (ep. 1) y series top pesan más.
    /// </summary>
    private async Task<List<Episode>> OrderByHeroAsync(List<Episode> episodes, CancellationToken ct)
    {
        if (episodes.Count <= 1) return episodes;

        Episode? hero = null;
        if (!string.IsNullOrWhiteSpace(aiSettings.ApiKey))
        {
            try
            {
                var list = string.Join("\n", episodes.Select((e, i) =>
                {
                    var genres = string.Join(", ", e.Series.SeriesGenres
                        .Select(sg => sg.Genre?.Name)
                        .Where(g => !string.IsNullOrWhiteSpace(g)));
                    return $"{i}: {e.Series.Title} — Episodio {e.EpisodeNumber}" +
                           (e.Series.Score is { } s ? $" — score {s}" : "") +
                           (genres.Length > 0 ? $" — {genres}" : "");
                }));
                var response = await gemini.GenerateAsync(
                    "Sos el community manager de un sitio de anime para LATAM. De la lista de episodios " +
                    "recién subidos, elegí EL más promocionable para la portada del post del día: estrenos " +
                    "(episodio 1) de series esperadas, franquicias grandes y series con score alto pesan más " +
                    "que un episodio intermedio de una serie de relleno. " +
                    "Respondé SOLO un JSON: {\"index\": <número de la lista>}",
                    list, useWebSearch: false, ct);

                using var doc = System.Text.Json.JsonDocument.Parse(response);
                var idx = doc.RootElement.GetProperty("index").GetInt32();
                if (idx >= 0 && idx < episodes.Count) hero = episodes[idx];
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Gemini hero-episode ranking failed — using heuristic");
            }
        }

        hero ??= episodes.OrderByDescending(e => HeuristicEpisodeScore(e.Series, e)).First();

        if (!ReferenceEquals(hero, episodes[0]))
            logger.LogInformation("Hero episode del batch: {Series} ep {Ep} (antes iba {First})",
                hero.Series.Title, hero.EpisodeNumber, episodes[0].Series.Title);

        return [hero, .. episodes.Where(e => !ReferenceEquals(e, hero))];
    }

    /// <summary>Score de promocionabilidad sin IA. Público para tests.</summary>
    public static int HeuristicEpisodeScore(Series series, Episode episode)
    {
        var score = 0;

        // Estreno de temporada = el evento a anunciar con bombos y platillos
        if (episode.EpisodeNumber == 1) score += 5;
        else if (episode.EpisodeNumber <= 3) score += 2;

        // Series bien puntuadas venden más
        if (series.Score >= 8m) score += 3;
        else if (series.Score >= 7m) score += 1;

        // Películas/especiales son eventos por sí mismos
        if (series.Type is "movie") score += 3;

        return score;
    }

    // ── Reel (motion card MP4, share_to_feed) ────────────────────────

    /// <summary>
    /// Publishes the animated motion-card Reel. Returns the hosted video URL on
    /// success (para que la story lo reuse), or null if it failed/was skipped.
    /// </summary>
    private async Task<string?> PublishReelAsync(Episode episode, CancellationToken ct)
    {
        var record = CreateRecord(episode, "reel");
        db.InstagramPosts.Add(record);

        try
        {
            // Música por IA (mood de la serie → track propio/CC). null = reel
            // silencioso. Va PRIMERO: el crédito CC BY se dibuja como texto
            // chico dentro del overlay del video (nunca en el caption).
            var music = settings.ReelMusicEnabled
                ? await musicService.SelectAndDownloadAsync(episode.Series, ct)
                : null;

            // Capas para motion graphics (fondo + texto animados por separado);
            // si el render por capas falla, tarjeta plana con zoom como fallback.
            byte[] background;
            byte[]? overlay;
            try
            {
                (background, overlay) = await imageService.GenerateStoryLayersAsync(
                    episode.Series, episode, music?.Track.Attribution, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Layered render failed — falling back to flat card");
                background = await imageService.GenerateStoryAsync(episode.Series, episode, ct);
                overlay = null;
            }

            var videoBytes = await videoService.GenerateMotionCardAsync(
                background, overlay, music?.Mp3, music?.Track.StartSeconds ?? 0, ct);

            var fileName = $"{episode.Series.Slug}-ep{episode.EpisodeNumber}-reel-{DateTime.UtcNow:yyyyMMddHHmmss}.mp4";
            var videoUrl = await api.UploadVideoAsync(videoBytes, fileName, ct);

            var items   = new List<(Series, Episode)> { (episode.Series, episode) };
            var caption = captionGen.GenerateCarouselCaption(items);

            var containerId = await api.CreateReelContainerAsync(videoUrl, caption, shareToFeed: true, ct: ct);
            await api.WaitForContainerReadyAsync(containerId, ct, VideoProcessingTimeout);
            var mediaId = await api.PublishContainerAsync(containerId, ct);

            record.Status      = "published";
            record.IgMediaId   = mediaId;
            record.Caption     = caption;
            record.PublishedAt = DateTime.UtcNow;

            logger.LogInformation("Published reel for {Series} ep {Ep} → {MediaId}",
                episode.Series.Title, episode.EpisodeNumber, mediaId);

            var comment = captionGen.GenerateEpisodeLinksComment(items);
            await PostFirstCommentAsync(mediaId, comment, ct);

            return videoUrl;
        }
        catch (FfmpegNotAvailableException ex)
        {
            // Entorno sin ffmpeg (dev local): no es un error del episodio — se
            // marca skipped para no reintentar y el resto del flujo sigue igual.
            record.Status       = "skipped";
            record.ErrorMessage = ex.Message[..Math.Min(ex.Message.Length, 500)];
            logger.LogWarning("Reel skipped — {Reason}", ex.Message);
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            record.Status       = "failed";
            record.ErrorMessage = ex.Message[..Math.Min(ex.Message.Length, 500)];
            logger.LogError(ex, "Failed to publish reel for episode {EpisodeId}", episode.Id);
            return null;
        }
        finally
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
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

    /// <param name="videoUrl">MP4 ya hosteado (el del reel) — si viene, la story
    /// es de video; si es null, imagen estática como siempre.</param>
    private async Task PublishStoryAsync(Episode episode, string? videoUrl, CancellationToken ct)
    {
        var record = CreateRecord(episode, "story");
        db.InstagramPosts.Add(record);

        try
        {
            var episodeUrl = $"{settings.SiteUrl}/series/{episode.Series.Slug}/{episode.EpisodeNumber}";

            string containerId;
            if (videoUrl is not null)
            {
                containerId = await api.CreateVideoStoryContainerAsync(videoUrl, episodeUrl, ct);
                await api.WaitForContainerReadyAsync(containerId, ct, VideoProcessingTimeout);
            }
            else
            {
                var imageBytes = await imageService.GenerateStoryAsync(episode.Series, episode, ct);
                var fileName   = BuildFileName(episode.Series.Slug, episode.EpisodeNumber, "story");
                var publicUrl  = await api.UploadImageAsync(imageBytes, fileName, ct);

                containerId = await api.CreateStoryContainerAsync(publicUrl, episodeUrl, ct);
                await api.WaitForContainerReadyAsync(containerId, ct);
            }

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
