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
///   • Máx. UN Reel de noticias por día (slideshow: cover + puntos clave + CTA,
///     con música por IA y share_to_feed) — la noticia MÁS RELEVANTE del pool
///     lo gana (dedup por ig_reel_media_id en las últimas 24 h). La noticia del
///     reel NO publica además el carrusel: sería la misma noticia dos veces en
///     el feed. Si el reel falla, el carrusel actúa de respaldo.
///   • A single feed post / carousel (1080×1080) para el resto.
///   • A story (1080×1920) siempre.
///
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
    TrailerDownloadService trailerService,
    NewsRewriteService rewriter,
    GeminiClient gemini,
    AiSettings aiSettings,
    IHttpClientFactory httpFactory,
    ILogger<AnimeNewsPublisherService> logger)
{
    // Meta procesa video asíncrono y puede tardar varios minutos
    private static readonly TimeSpan VideoProcessingTimeout = TimeSpan.FromMinutes(6);

    /// <param name="items">Pool de candidatos pendientes (puede ser mayor a
    /// MaxPerRun — acá se capa). Si el Reel del día está disponible, la noticia
    /// MÁS RELEVANTE del pool (IA/heurística) se procesa primero y se lo lleva.</param>
    public async Task PublishPendingAsync(
        IReadOnlyList<AnimeNewsItem> items, CancellationToken ct = default)
    {
        if (!igSettings.IsConfigured)
        {
            logger.LogInformation("Instagram not configured — skipping news publisher");
            return;
        }
        if (items.Count == 0) return;

        var batch = await SelectBatchAsync(items, ct);
        logger.LogInformation("AnimeNews: publishing {Count} news item(s) to Instagram ({Pool} candidatas)",
            batch.Count, items.Count);

        foreach (var item in batch)
        {
            if (ct.IsCancellationRequested) break;
            await PublishItemAsync(item, ct);
        }
    }

    // ── Selección del batch (la más relevante primero si hay reel del día) ────

    private async Task<IReadOnlyList<AnimeNewsItem>> SelectBatchAsync(
        IReadOnlyList<AnimeNewsItem> items, CancellationToken ct)
    {
        if (items.Count <= 1 || !igSettings.NewsReelEnabled)
            return items.Take(newsSettings.MaxPerRun).ToList();

        bool reelAvailable;
        try
        {
            reelAvailable = !await db.AnimeNewsItems.AnyAsync(
                n => n.IgReelMediaId != null && n.IgPostedAt >= DateTime.UtcNow.AddHours(-24), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Ante un fallo de DB, no arriesgar un 2do reel del día
            logger.LogWarning(ex, "AnimeNews: dedup query falló — asumo reel ya publicado hoy");
            reelAvailable = false;
        }

        if (!reelAvailable)
            return items.Take(newsSettings.MaxPerRun).ToList();

        var best = await PickMostRelevantAsync(items, ct);
        logger.LogInformation("AnimeNews: noticia del día para el reel → \"{Title}\"", Truncate(best.Title, 80));

        return items
            .OrderByDescending(i => ReferenceEquals(i, best) ? 1 : 0)
            .ThenByDescending(i => i.PublishedAt)
            .Take(newsSettings.MaxPerRun)
            .ToList();
    }

    /// <summary>Gemini elige la noticia más relevante del pool; fallback heurístico por keywords.</summary>
    private async Task<AnimeNewsItem> PickMostRelevantAsync(
        IReadOnlyList<AnimeNewsItem> items, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(aiSettings.ApiKey))
        {
            try
            {
                var list = string.Join("\n", items.Select((n, i) =>
                    $"{i}: {n.Title}" + (string.IsNullOrWhiteSpace(n.Summary) ? "" : $" — {Truncate(n.Summary!, 120)}")));
                var response = await gemini.GenerateAsync(
                    "Sos el editor jefe de un medio de anime en español para LATAM. De la lista, elegí LA noticia " +
                    "más relevante/viral para el video destacado del día (estrenos grandes, anuncios bomba, " +
                    "fallecimientos de figuras, polémicas fuertes pesan más que curiosidades menores). " +
                    "A igual peso, preferí noticias con material audiovisual oficial (anuncio de tráiler, " +
                    "teaser, nueva temporada o película): el video destacado puede incrustar ese tráiler. " +
                    "Respondé SOLO un JSON: {\"index\": <número de la lista>}",
                    list, useWebSearch: false, ct);

                using var doc = System.Text.Json.JsonDocument.Parse(response);
                var idx = doc.RootElement.GetProperty("index").GetInt32();
                if (idx >= 0 && idx < items.Count) return items[idx];
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Gemini relevance ranking failed — using heuristic");
            }
        }

        // Heurística: mayor score gana; empate → la más nueva (orden de entrada)
        return items.OrderByDescending(n => HeuristicNewsScore($"{n.Title} {n.Summary}")).First();
    }

    /// <summary>Score de relevancia por keywords cuando no hay IA. Público para tests.</summary>
    public static int HeuristicNewsScore(string text)
    {
        var t = text.ToLowerInvariant();

        // Anuncios grandes / lanzamientos
        var score = new[] { "estreno", "estrena", "tráiler", "trailer", "temporada", "película",
                             "pelicula", "confirmado", "confirma", "anuncia", "live-action", "adaptación", "adaptacion" }
            .Count(t.Contains) * 3;
        // Noticias de peso (luto / polémicas fuertes)
        score += new[] { "fallec", "muere", "murió", "homenaje", "demanda", "cancel" }
            .Count(t.Contains) * 3;
        // Franquicias enormes / títulos top del momento: empujón extra
        score += new[] { "one piece", "naruto", "dragon ball", "jujutsu", "chainsaw", "attack on titan",
                          "shingeki", "demon slayer", "kimetsu", "ghibli", "evangelion",
                          "black clover", "spy x family", "spy family", "frieren", "solo leveling",
                          "dandadan", "blue lock", "my hero", "boku no hero", "bleach", "re:zero",
                          "mushoku tensei", "witch hat", "sword art", "tokyo revengers", "oshi no ko" }
            .Count(t.Contains) * 2;
        // Menores
        score += new[] { "colaboración", "colaboracion", "evento", "figura", "manga" }
            .Count(t.Contains);

        return score;
    }

    private async Task PublishItemAsync(AnimeNewsItem item, CancellationToken ct)
    {
        try
        {
            // Gather all usable article media (cover + in-body images + trailer).
            var (images, trailerUrl) = await GatherMediaAsync(item, ct);

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

            // ── Reel diario PRIMERO (máx 1 por 24 h) ──────────────────────
            // Si esta noticia gana el reel, NO se publica además el carrusel:
            // el reel (share_to_feed=true) ya la muestra en el feed con las
            // mismas slides animadas — dos posts de la misma noticia es spam.
            string? reelMediaId = null;
            if (igSettings.NewsReelEnabled)
            {
                try
                {
                    var reelRecently = await db.AnimeNewsItems.AnyAsync(
                        n => n.IgReelMediaId != null && n.IgPostedAt >= DateTime.UtcNow.AddHours(-24), ct);
                    if (!reelRecently)
                        reelMediaId = await PublishReelAsync(item, content, images, trailerUrl, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "AnimeNews: reel dedup check failed — skipping reel this run");
                }
            }

            // ── Feed (carousel: cover + content slides + closing CTA) ──
            // Solo si el reel NO salió — si falló, el carrusel es el respaldo.
            string? feedMediaId = null;
            if (reelMediaId is null)
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
            catch (Exception ex) when (!ct.IsCancellationRequested)
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
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex,
                    "AnimeNews: failed to publish story for {Title}", Truncate(item.Title, 60));
            }

            // Mark as published (even if only one of the formats succeeded)
            var anyPublished = feedMediaId is not null || storyMediaId is not null || reelMediaId is not null;
            item.IgPostStatus    = anyPublished ? "published" : "failed";
            item.IgFeedMediaId   = feedMediaId;
            item.IgStoryMediaId  = storyMediaId;
            item.IgReelMediaId   = reelMediaId;
            item.IgPostedAt      = anyPublished ? DateTime.UtcNow : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
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

    // ── Reel de noticias (slideshow + música por IA) ─────────────────────────

    /// <summary>
    /// Publishes the news Reel as a SLIDESHOW: cover + puntos clave + CTA (las
    /// mismas escenas editoriales del carrusel, en 9:16) con Ken Burns alternado,
    /// crossfades y track según el mood. El cover 9:16 va como cover_url del
    /// reel — sin él, IG usaba el primer frame (negro por el fade-in) como
    /// miniatura del feed. Best-effort — un fallo acá degrada al carrusel.
    /// </summary>
    private async Task<string?> PublishReelAsync(
        AnimeNewsItem item, NewsContent content, IReadOnlyList<string> images,
        string? trailerUrl, CancellationToken ct)
    {
        try
        {
            // Cadena de formatos, de mejor a más simple:
            //   1. Tráiler CON SU AUDIO ORIGINAL + titular + slides informativas
            //      (el embebido del artículo si pasa la validación de español,
            //      o el ENCONTRADO por búsqueda IA en YouTube)
            //   2. Slideshow de escenas (cover + puntos clave + CTA, con música)
            //   3. Motion-card de capas (tarjeta única, con música)
            byte[]? videoBytes = null;
            byte[] coverJpeg;

            if (igSettings.TrailerReelEnabled)
            {
                // El embebido del artículo se valida (kudasai suele embeber el PV
                // japonés — el requisito es español latino); si no pasa, se busca
                // la versión latina en YouTube.
                var candidate = trailerUrl is null
                    ? null
                    : await trailerService.ValidateAsync(trailerUrl, ct);
                if (candidate is null && igSettings.TrailerSearchEnabled)
                    candidate = await SearchTrailerAsync(item, content, ct);

                var clipPath = candidate is null ? null : await trailerService.DownloadAsync(candidate.Url, ct);
                if (clipPath is not null)
                {
                    try
                    {
                        // Slides informativas para DESPUÉS del tráiler: puntos
                        // clave + CTA (sin cover — el tráiler es la apertura).
                        // Sin crédito de música: suena el audio del tráiler.
                        var allSlides = await imageService.GenerateReelSlidesAsync(
                            item, content, images, maxKeyPoints: 2, musicCredit: null, ct: ct);
                        var infoSlides = allSlides.Skip(1).ToList();

                        var (bg, overlay) = imageService.GenerateVideoReelLayers(content);
                        videoBytes = await videoService.GenerateTrailerReelAsync(
                            clipPath, bg, overlay, infoSlides, candidate!.DurationSeconds, ct);
                        logger.LogInformation("AnimeNews: reel con TRÁILER para \"{Title}\" ({Url})",
                            Truncate(item.Title, 60), candidate.Url);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && ex is not FfmpegNotAvailableException)
                    {
                        logger.LogWarning(ex, "Trailer reel render failed — falling back to slideshow");
                    }
                    finally
                    {
                        TrailerDownloadService.CleanUp(clipPath);
                    }
                }
            }

            if (videoBytes is null)
            {
                // La música CC/propia es SOLO para el slideshow — el reel de
                // tráiler usa el audio original del video. El crédito CC BY va
                // como texto chico dentro del video (nunca en el caption).
                var music = await musicService.SelectAndDownloadForNewsAsync(
                    content.Headline, content.Lede, item.RssGuid, ct);
                var musicCredit = music?.Track.Attribution;

                try
                {
                    var slides = await imageService.GenerateReelSlidesAsync(
                        item, content, images, maxKeyPoints: 3, musicCredit: musicCredit, ct: ct);
                    coverJpeg  = slides[0];
                    videoBytes = await videoService.GenerateSlideshowAsync(
                        slides, music?.Mp3, music?.Track.StartSeconds ?? 0, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is not FfmpegNotAvailableException)
                {
                    logger.LogWarning(ex, "Slideshow render failed — falling back to single motion card");
                    var (background, overlay) = await imageService.GenerateStoryLayersAsync(
                        item, content, images, musicCredit: musicCredit, ct: ct);
                    coverJpeg  = await imageService.GenerateStoryAsync(item, content, images, ct);
                    videoBytes = await videoService.GenerateMotionCardAsync(
                        background, overlay, music?.Mp3, music?.Track.StartSeconds ?? 0, ct);
                }
            }
            else
            {
                // El cover del reel de tráiler es la tarjeta editorial con la foto
                coverJpeg = await imageService.GenerateStoryAsync(item, content, images, ct);
            }

            var baseName = $"news-{item.SourceKey}-{item.Id.ToString("N")[..8]}";
            var videoUrl = await api.UploadVideoAsync(videoBytes, $"{baseName}-reel.mp4", ct);

            // Cover best-effort: si el upload falla, el reel sale igual (sin miniatura linda)
            string? coverUrl = null;
            try { coverUrl = await api.UploadImageAsync(coverJpeg, $"{baseName}-reel-cover.jpg", ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Reel cover upload failed — publishing without cover_url");
            }

            var caption = BuildCaption(content);
            var containerId = await api.CreateReelContainerAsync(videoUrl, caption, shareToFeed: true, coverUrl, ct);
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
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "AnimeNews: failed to publish reel for {Title}", Truncate(item.Title, 60));
            return null;
        }
    }

    // ── Búsqueda del tráiler por IA ──────────────────────────────────────────

    /// <summary>
    /// Cuando el artículo no embebe un tráiler (el caso típico), lo BUSCA en
    /// YouTube: Gemini decide si la noticia amerita video (anuncio de tráiler/
    /// teaser/temporada/película — NO novelas, manga, figuras o luto) y arma la
    /// query; sin IA cae a la heurística por keywords. Best-effort → null.
    /// </summary>
    private async Task<TrailerCandidate?> SearchTrailerAsync(
        AnimeNewsItem item, NewsContent content, CancellationToken ct)
    {
        string? query = null;

        if (!string.IsNullOrWhiteSpace(aiSettings.ApiKey))
        {
            try
            {
                var response = await gemini.GenerateAsync(
                    "Sos productor de video de un noticiero de anime. Decidí si para esta noticia corresponde " +
                    "buscar un tráiler oficial en YouTube para usarlo de fondo en el reel. Corresponde SOLO " +
                    "cuando la noticia anuncia material audiovisual: nuevo tráiler/teaser/PV, nueva temporada, " +
                    "película, adaptación a anime o fecha de estreno. NO corresponde para novelas o manga sin " +
                    "anime confirmado, figuras, eventos, rankings, fallecimientos ni polémicas. Si corresponde, " +
                    "armá la búsqueda de YouTube apuntando a la VERSIÓN EN ESPAÑOL LATINO (doblada o subtitulada " +
                    "— la audiencia es LATAM y canales como Crunchyroll en Español suben esa versión): " +
                    "nombre de la obra + \"tráiler oficial español latino\" " +
                    "(ej.: \"Solo Leveling temporada 2 tráiler oficial español latino\"). " +
                    "Respondé SOLO un JSON: {\"buscar\": true|false, \"query\": \"...\"}",
                    $"Titular: {item.Title}\nResumen: {Truncate(item.Summary ?? content.Lede ?? string.Empty, 400)}",
                    useWebSearch: false, ct);

                using var doc = System.Text.Json.JsonDocument.Parse(response);
                if (!doc.RootElement.TryGetProperty("buscar", out var buscar) || !buscar.GetBoolean())
                {
                    // La IA decidió que la noticia no amerita video — respetarla,
                    // no caer a la heurística (es menos precisa).
                    logger.LogInformation("AnimeNews: la noticia no amerita tráiler según IA — slideshow");
                    return null;
                }
                if (doc.RootElement.TryGetProperty("query", out var q))
                    query = q.GetString();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Gemini trailer decision failed — using heuristic");
            }
        }

        query ??= HeuristicTrailerQuery(item.Title);
        if (string.IsNullOrWhiteSpace(query)) return null;

        logger.LogInformation("AnimeNews: buscando tráiler en YouTube → \"{Query}\"", query);
        return await trailerService.SearchAsync(query!, ct);
    }

    /// <summary>
    /// Fallback sin IA: solo busca cuando el titular anuncia material
    /// audiovisual; la query es el titular + "tráiler oficial español latino"
    /// (el nombre de la obra ya viene en el titular, y el sufijo apunta a la
    /// versión doblada/subtitulada para LATAM). Público estático para tests.
    /// </summary>
    public static string? HeuristicTrailerQuery(string title)
    {
        var t = title.ToLowerInvariant();
        var audiovisual = new[] { "tráiler", "trailer", "teaser", "avance", "temporada",
                                  "película", "pelicula", "live-action", "adaptación", "adaptacion", "estreno" };
        return audiovisual.Any(t.Contains) ? $"{title} tráiler oficial español latino" : null;
    }

    // ── Caption ──────────────────────────────────────────────────────────────

    // Instagram caption limit is 2200 characters.
    private const int IgCaptionMaxChars = 2200;

    // Always-on base hashtags. No "ñ" (Instagram mangles "#animeespañol") and no spaces.
    private static readonly string[] BaseHashtags =
        ["anime", "animelatino", "animenoticias", "manga", "otaku", "sheicobanime"];

    /// <summary>
    /// Builds the Instagram caption from the already-rewritten content: a headline line, the
    /// original editorial body (the rewrite — never the source text; ahora más largo/profundo
    /// que las slides), a CTA, smart hashtags and the handle. El cuerpo se presupuesta para que
    /// los hashtags y el @ nunca queden fuera del límite de IG (2200). La música no lleva línea
    /// en el caption: el crédito CC va como texto chico dentro del video.
    /// </summary>
    private string BuildCaption(NewsContent content)
    {
        var header = $"📰 {content.Headline.Trim()}\n\n";

        // El "pie" fijo (CTA + hashtags + handle) se arma primero para saber cuánto
        // espacio real queda para el cuerpo — así un caption largo nunca corta los
        // hashtags.
        var tail = new StringBuilder();
        tail.Append("🔔 Seguinos para más noticias de anime\n");
        tail.Append("▶️ Mirá anime gratis · Link en la bio\n\n");
        tail.Append(BuildHashtags(content.Hashtags));
        if (!string.IsNullOrWhiteSpace(igSettings.Handle))
            tail.Append("\n\n@").Append(igSettings.Handle);

        var bodyBudget = IgCaptionMaxChars - header.Length - tail.Length;

        var body = content.Caption?.Trim() ?? string.Empty;
        if (bodyBudget > 0 && body.Length > bodyBudget)
            body = body[..bodyBudget].TrimEnd();

        var sb = new StringBuilder();
        sb.Append(header);
        if (body.Length > 0) sb.Append(body).Append("\n\n");
        sb.Append(tail);

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
    /// Collects every usable media for the item: the stored cover plus any in-body
    /// images from the article page (best-effort re-fetch), AND the first embedded
    /// YouTube trailer if any — el reel lo usa de fondo cuando existe.
    /// </summary>
    private async Task<(IReadOnlyList<string> Images, string? TrailerUrl)> GatherMediaAsync(
        AnimeNewsItem item, CancellationToken ct)
    {
        var images = new List<string>();
        string? trailerUrl = null;
        if (!string.IsNullOrWhiteSpace(item.ImageUrl)) images.Add(item.ImageUrl!);
        if (string.IsNullOrWhiteSpace(item.ArticleUrl))
            return (images, null);

        try
        {
            var http = httpFactory.CreateClient("news-rss");
            using var resp = await http.GetAsync(item.ArticleUrl, ct);
            if (resp.IsSuccessStatusCode)
            {
                var html = await resp.Content.ReadAsStringAsync(ct);
                images.AddRange(AnimeNewsFeedService.ExtractArticleImages(html));
                trailerUrl = AnimeNewsFeedService.ExtractArticleVideoUrl(html);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "AnimeNews: could not gather extra media for {Title}", Truncate(item.Title, 50));
        }

        return (images.Distinct().Take(6).ToList(), trailerUrl);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string SlideFileName(AnimeNewsItem item, int index) =>
        $"news-{item.SourceKey}-{item.Id.ToString("N")[..8]}-{index}.jpg";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
