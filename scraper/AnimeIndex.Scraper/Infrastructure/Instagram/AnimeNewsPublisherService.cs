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
///   • Reel de noticias (slideshow/tráiler: cover + puntos clave + CTA, con
///     música por IA y share_to_feed) — la noticia MÁS RELEVANTE del pool lo
///     gana. El formato de cada corrida lo decide AnimeNews__RunFormat (lo
///     setea el cron: desde sep-2026 las 7 corridas del día son reel — el feed
///     medía 2 % del alcance y 1 seguidor en 4 meses); sin ese env var rige el
///     dedup original de máx. un reel por 24 h. La noticia del
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

    // ── Selección del batch (la más relevante primero si la corrida publica reel) ──

    private async Task<IReadOnlyList<AnimeNewsItem>> SelectBatchAsync(
        IReadOnlyList<AnimeNewsItem> items, CancellationToken ct)
    {
        if (newsSettings.IsPostRun)
        {
            // Corrida de carrusel común (sin video): las noticias que anuncian
            // material audiovisual (tráiler/opening/corto) se POSTERGAN para que
            // les toque un slot de reel — una noticia de opening publicada como
            // carrusel promete un video que el post no puede mostrar (pasó en
            // prod, jul-2026). Si el pool solo tiene audiovisuales, salen igual.
            return items
                .OrderBy(i => HasAudiovisualSignal(i.Title) ? 1 : 0)
                .Take(newsSettings.MaxPerRun)
                .ToList();
        }

        if (items.Count <= 1 || !igSettings.NewsReelEnabled)
            return items.Take(newsSettings.MaxPerRun).ToList();

        bool reelAvailable;
        if (newsSettings.IsReelRun)
        {
            // Corrida de reel del cron: el horario ya espacia los reels, no hay dedup.
            reelAvailable = true;
        }
        else
        {
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
                    "teaser, nueva temporada o película, opening/ending o video musical, corto o video " +
                    "especial): el video destacado puede incrustar ese material. " +
                    "Respondé SOLO un JSON: {\"index\": <número de la lista>}",
                    list, useWebSearch: false, ct);

                using var doc = System.Text.Json.JsonDocument.Parse(response);
                var idx = doc.RootElement.GetProperty("index").GetInt32();
                if (idx >= 0 && idx < items.Count) return items[idx];
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "AnimeNews: ranking por IA falló — heurística");
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
        // Material audiovisual que el reel puede incrustar (opening/corto/MV)
        score += new[] { "opening", "ending", "video musical", "corto animado" }
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
            var (images, trailerUrl, tweetUrl) = await GatherMediaAsync(item, ct);

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

            // ── Reel PRIMERO ──────────────────────────────────────────────
            // Si esta noticia gana el reel, NO se publica además el carrusel:
            // el reel (share_to_feed=true) ya la muestra en el feed con las
            // mismas slides animadas — dos posts de la misma noticia es spam.
            // En corridas "reel" del cron se publica directo (el horario ya
            // espacia); en corridas "post" no se intenta; sin formato rige el
            // dedup original de máx. 1 reel por 24 h.
            string? reelMediaId = null;
            if (igSettings.NewsReelEnabled && !newsSettings.IsPostRun)
            {
                if (newsSettings.IsReelRun)
                {
                    reelMediaId = await PublishReelAsync(item, content, images, trailerUrl, tweetUrl, ct);
                }
                else
                {
                    try
                    {
                        var reelRecently = await db.AnimeNewsItems.AnyAsync(
                            n => n.IgReelMediaId != null && n.IgPostedAt >= DateTime.UtcNow.AddHours(-24), ct);
                        if (!reelRecently)
                            reelMediaId = await PublishReelAsync(item, content, images, trailerUrl, tweetUrl, ct);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "AnimeNews: reel dedup check failed — skipping reel this run");
                    }
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
        string? trailerUrl, string? articleTweetUrl, CancellationToken ct)
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
                // japonés — el requisito es español latino); si no pasa, la
                // búsqueda por IA decide el TIPO de video (tráiler/opening/corto)
                // y puede rescatar ese mismo embebido cuando el idioma no aplica.
                var embedded = trailerUrl is null
                    ? null
                    : await trailerService.ValidateAsync(trailerUrl, ct: ct);
                var plan = embedded is not null
                    ? new NewsVideoPlan([embedded], true,
                        HeuristicVideoQuery(item.Title)?.Query ?? "", null,
                        HeuristicVideoQuery(item.Title)?.Kind ?? NewsVideoKind.Trailer)
                    : igSettings.TrailerSearchEnabled
                        ? await FindNewsVideoAsync(item, content, trailerUrl, ct)
                        : NewsVideoPlan.None;

                // Se BAJA POR LA LISTA hasta que uno entre. Antes se probaba un
                // solo candidato y si ese moría el reel salía sin video aunque
                // hubiera alternativas buenas: el 24-ago-2026 la búsqueda de
                // Tokyo Revengers devolvió 6 tráilers válidos, el elegido comió
                // el bot-check en sus dos intentos y los otros 5 ni se tocaron.
                TrailerCandidate? candidate = null;
                string? clipPath = null;
                foreach (var option in plan.Candidates.Take(igSettings.MaxVideoCandidates))
                {
                    clipPath = await trailerService.DownloadAsync(option.Url, ct);
                    if (clipPath is not null) { candidate = option; break; }
                    logger.LogInformation(
                        "AnimeNews: no se pudo bajar {Url} — probando el próximo candidato", option.Url);
                }

                // Respaldo X/Twitter (18-jul-2026): la descarga de YouTube está
                // bloqueada desde CI (34 combos FAIL) pero X no bloquea a los
                // runners y los tráilers oficiales también se publican ahí.
                // Corre siempre que la noticia AMERITE video — aunque la búsqueda
                // de YouTube no haya dado candidato (hueco real 18-jul: el
                // opening de Suis/Yorushika existía y los respaldos ni corrieron).
                if (clipPath is null && plan.WantsVideo && igSettings.TweetVideoFallback)
                {
                    // 1) El tweet EMBEBIDO en el artículo (kudasai/anmosugoi lo
                    //    traen en los anuncios): procedencia = relevancia, mismo
                    //    trato que el YouTube embebido — solo se exige que tenga
                    //    un VIDEO de duración de clip (muchos embeds son tweets
                    //    de texto). 2) Sin embebido usable, la IA con grounding.
                    var tweet = articleTweetUrl is null
                        ? null
                        : await trailerService.ValidateOfficialPostAsync(
                              articleTweetUrl, TrailerDownloadService.SubjectFromTitle(item.Title),
                              requireTrustSignal: false, ct);
                    tweet ??= await FindTweetVideoAsync(item, content, ct);
                    var tweetClip = tweet is null ? null : await trailerService.DownloadAsync(tweet.Url, ct);
                    if (tweetClip is not null)
                    {
                        logger.LogInformation("AnimeNews: video del post oficial de X como respaldo → {Url}", tweet!.Url);
                        if (candidate?.SubtitlesPath is not null)
                            TrailerDownloadService.CleanUp(candidate.SubtitlesPath);
                        candidate = tweet;
                        clipPath = tweetClip;
                    }
                }

                // Última red: bilibili (verificado 18-jul: búsqueda y descarga
                // pasan desde los runners; los PV/openings de anime están casi
                // todos ahí). Query limpia: obra + "PV" para tráilers, la del
                // plan (artista+canción) para temas.
                if (clipPath is null && plan.WantsVideo && igSettings.TweetVideoFallback)
                {
                    var obra = TrailerDownloadService.SubjectFromTitle(item.Title);
                    var biliSubject = $"{plan.Subject} {obra}".Trim();
                    var biliQuery = plan.Kind == NewsVideoKind.Trailer ? $"{obra} PV" : plan.Query;
                    if (biliQuery.Trim().Length > 0)
                    {
                        logger.LogInformation("AnimeNews: última red — buscando en bilibili → \"{Query}\"", biliQuery);
                        var bili = await trailerService.SearchAsync(
                            biliQuery, requireSpanish: false, plan.Kind, biliSubject,
                            searchPrefix: "bilisearch", ct: ct);
                        var biliClip = bili is null ? null : await trailerService.DownloadAsync(bili.Url, ct);
                        if (biliClip is not null)
                        {
                            logger.LogInformation("AnimeNews: video de bilibili como última red → {Url}", bili!.Url);
                            if (candidate?.SubtitlesPath is not null)
                                TrailerDownloadService.CleanUp(candidate.SubtitlesPath);
                            candidate = bili;
                            clipPath = biliClip;
                        }
                    }
                }

                // La noticia AMERITABA video y la escalera entera (YouTube → X →
                // bilibili) se quedó sin nada: es una FALLA, no el camino normal.
                // Va como Warning porque todo acá es best-effort y la corrida
                // termina verde igual — sin esta línea, "el reel salió sin video"
                // no deja ninguna señal y el post-mortem arranca bajando los logs
                // de 40 corridas a mano (semana del 31-ago-2026).
                if (clipPath is null && plan.WantsVideo)
                    logger.LogWarning(
                        "AnimeNews: REEL SIN VIDEO — la noticia amerita {Kind} pero ningún candidato bajó " +
                        "(query \"{Query}\", {Count} candidato(s) de YouTube, respaldos X y bilibili sin suerte): \"{Title}\"",
                        plan.Kind, plan.Query, plan.Candidates.Count, Truncate(item.Title, 60));

                if (clipPath is not null)
                {
                    try
                    {
                        // Slides informativas para DESPUÉS del tráiler: desde
                        // sep-2026, SOLO el CTA de cierre (maxKeyPoints: 0 →
                        // cover + CTA, y el cover se descarta porque el tráiler
                        // es la apertura). Los puntos clave que iban acá vivían
                        // en los últimos 10,5 s de un reel con 12 % de retención
                        // mediana: no los veía nadie, y estirar el video hundía
                        // la finalización. El contenido no se pierde — el titular
                        // va quemado sobre el video y el cuerpo entero, en el
                        // caption. Sin crédito de música: suena el audio del tráiler.
                        var allSlides = await imageService.GenerateReelSlidesAsync(
                            item, content, images, maxKeyPoints: 0, musicCredit: null, ct: ct);
                        var infoSlides = allSlides.Skip(1).ToList();

                        var (hook, overlay) = imageService.GenerateVideoReelLayers(content);
                        videoBytes = await videoService.GenerateTrailerReelAsync(
                            clipPath, hook, overlay, infoSlides, candidate!.DurationSeconds,
                            candidate.SubtitlesPath, ct);
                        logger.LogInformation("AnimeNews: reel con TRÁILER para \"{Title}\" ({Url}{Subs})",
                            Truncate(item.Title, 60), candidate.Url,
                            candidate.SubtitlesPath is null ? "" : ", subs es quemados");
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && ex is not FfmpegNotAvailableException)
                    {
                        logger.LogWarning(ex, "Trailer reel render failed — falling back to slideshow");
                    }
                    finally
                    {
                        TrailerDownloadService.CleanUp(clipPath);
                        // candidate no puede ser null acá: clipPath solo se obtiene de
                        // candidate.Url (línea 321). El compilador no extiende esa
                        // garantía al bloque finally, de ahí el null-forgiving.
                        if (candidate!.SubtitlesPath is not null)
                            TrailerDownloadService.CleanUp(candidate.SubtitlesPath);
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
            // Crear + esperar + publicar CON reintento: el 6-sep-2026 dos reels
            // ya renderizados murieron en "Container ... terminal status: ERROR"
            // mientras el video seguía sirviéndose bien desde Cloudinary. El
            // container quemado no se recupera, se crea uno nuevo con la misma URL.
            var mediaId = await api.CreateWaitPublishAsync(
                token => api.CreateReelContainerAsync(videoUrl, caption, shareToFeed: true, coverUrl, token),
                VideoProcessingTimeout, ct);

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

    // ── Búsqueda del video de la noticia por IA ─────────────────────────────

    /// <summary>
    /// Cuando el artículo no embebe un video usable (el caso típico), lo BUSCA
    /// en YouTube: Gemini decide si la noticia amerita video y de QUÉ TIPO —
    /// tráiler, tema musical (opening/ending/MV) o corto/video especial — y
    /// arma la query; sin IA cae a la heurística por keywords. Para temas y
    /// cortos el requisito de español no aplica (el video ES la noticia: una
    /// canción japonesa o una animación) y el embebido del artículo se rescata
    /// si es un upload oficial. Best-effort → null (slideshow).
    /// </summary>
    private async Task<NewsVideoPlan> FindNewsVideoAsync(
        AnimeNewsItem item, NewsContent content, string? embeddedUrl, CancellationToken ct)
    {
        string? query = null;
        string? subject = null;
        var kind = NewsVideoKind.Trailer;

        if (!string.IsNullOrWhiteSpace(aiSettings.ApiKey))
        {
            string? response = null;
            try
            {
                response = await gemini.GenerateAsync(
                    "Sos productor de video de un noticiero de anime. Decidí si para esta noticia corresponde " +
                    "buscar en YouTube un video OFICIAL para usarlo de fondo en el reel, y de qué tipo: " +
                    "\"trailer\" si la noticia anuncia un tráiler/teaser/PV, nueva temporada, película, " +
                    "adaptación a anime o fecha de estreno; " +
                    "\"tema\" si presenta un opening, ending, tema musical o video musical (MV); " +
                    "\"corto\" si presenta un corto animado, video especial o de aniversario. " +
                    "NO corresponde para novelas o manga sin anime confirmado, figuras, eventos, rankings, " +
                    "fallecimientos ni polémicas. \"obra\": el nombre EXACTO de la obra tal como aparecería " +
                    "en el título del upload oficial de YouTube, en romaji/inglés (para \"tema\" SIEMPRE el " +
                    "ARTISTA: el upload oficial de un MV/opening lleva al artista y la canción, no a la " +
                    "serie) — se usa para verificar que el video encontrado sea de ESA obra y no de otra. " +
                    "La query: para \"trailer\", la obra + \"tráiler oficial español latino\" (la audiencia " +
                    "es LATAM y canales como Crunchyroll en Español suben esa versión — ej.: \"Solo Leveling " +
                    "temporada 2 tráiler oficial español latino\"); para \"tema\", SOLO artista + canción + " +
                    "tipo (ej.: \"MYTH & ROID Why? RED induction MV\", \"Ikimonogakari Sayonara Lara Music " +
                    "Video\") — NUNCA el título licenciado de la serie en la query: los videos fan " +
                    "(lyrics/reaction/covers) lo repiten en sus títulos y entierran al upload oficial, que " +
                    "titula en japonés/romaji; si no conocés artista y canción, la obra en romaji + " +
                    "\"opening\" u \"ending\" a secas; para \"corto\", la obra + qué video es, como lo " +
                    "titularía el upload oficial (ej.: \"Mob Psycho 100 anniversary special movie\"). " +
                    "Respondé SOLO un JSON: {\"buscar\": true|false, \"tipo\": \"trailer\"|\"tema\"|\"corto\", " +
                    "\"obra\": \"...\", \"query\": \"...\"}",
                    $"Titular: {item.Title}\nResumen: {Truncate(item.Summary ?? content.Lede ?? string.Empty, 400)}",
                    useWebSearch: false, ct);

                // ExtractJsonObject: Gemma (fallback de cuota) envuelve el JSON en
                // prosa, y hasta flash-lite con JSON mode metió texto extra tras el
                // objeto — parsear crudo tiró 4 corridas a la heurística (16-17 jul).
                using var doc = System.Text.Json.JsonDocument.Parse(GeminiClient.ExtractJsonObject(response));
                if (!doc.RootElement.TryGetProperty("buscar", out var buscar) || !buscar.GetBoolean())
                {
                    // La IA dice que no amerita video. Se la respeta SALVO que el
                    // titular anuncie material audiovisual explícito: "Orbitals
                    // estrena video musical junto con detalles de vinilo con
                    // opening y ending" recibió buscar:false y el reel salió sin
                    // video (18-ago-2026). Peor todavía, ese camino dejaba
                    // WantsVideo=false y salteaba TODA la escalera de respaldos.
                    var announced = HeuristicVideoQuery(item.Title);
                    if (announced is null)
                    {
                        logger.LogInformation("AnimeNews: la noticia no amerita video según IA — slideshow");
                        return NewsVideoPlan.None;
                    }

                    logger.LogInformation(
                        "AnimeNews: la IA dijo que no amerita video pero el titular anuncia {Kind} — se busca igual → \"{Query}\"",
                        announced.Value.Kind, announced.Value.Query);
                    (query, kind) = announced.Value;
                }
                else
                {
                    if (doc.RootElement.TryGetProperty("query", out var q))
                        query = q.GetString();
                    if (doc.RootElement.TryGetProperty("obra", out var o))
                        subject = o.GetString();
                    if (doc.RootElement.TryGetProperty("tipo", out var tipo))
                        kind = ParseVideoKind(tipo.GetString());
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Warning (no Debug): si la IA falla acá — cuota agotada, típico —
                // la decisión cae a la heurística y queremos verlo en los logs.
                // El response crudo va al log: los JsonReaderException de prod
                // fueron indescifrables sin él.
                logger.LogWarning(ex,
                    "AnimeNews: decisión de video por IA falló — heurística. Respuesta: {Response}",
                    response is null ? "(sin respuesta)" : Truncate(response, 300));
            }
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            var heuristic = HeuristicVideoQuery(item.Title);
            if (heuristic is null)
            {
                logger.LogInformation("AnimeNews: el titular no anuncia material audiovisual — slideshow");
                return NewsVideoPlan.None;
            }
            (query, kind) = heuristic.Value;
        }

        // La noticia AMERITA video: pase lo que pase con YouTube, el plan viaja
        // al caller para que la escalera de respaldos (tweet del artículo → X
        // vía IA → bilibili) corra igual — la garantía "si el video existe, el
        // reel sale con video" depende de esto (hueco real 18-jul: la búsqueda
        // no dio candidato y los respaldos ni corrieron).
        var plan = new NewsVideoPlan([], true, query!, subject, kind);

        // ── Temas (opening/ending/MV) y cortos: el video ES la noticia ──────
        // El idioma no aplica (canción japonesa / animación) pero el upload
        // tiene que ser oficial. El embebido del artículo es el candidato
        // exacto — se prueba antes que la búsqueda.
        if (kind is NewsVideoKind.ThemeSong or NewsVideoKind.Short)
        {
            var candidates = new List<TrailerCandidate>();

            var embedded = embeddedUrl is null
                ? null
                : await trailerService.ValidateAsync(embeddedUrl, requireSpanish: false, kind,
                      trustProvenance: true, ct);
            if (embedded is not null)
            {
                logger.LogInformation("AnimeNews: video {Kind} embebido del articulo aceptado (upload oficial)", kind);
                candidates.Add(embedded);
            }

            // La busqueda corre IGUAL con embebido aceptado: cuesta ~2 s y deja
            // suplentes por si el embebido no se puede bajar (bot-check, borrado,
            // region-lock). Con video obligatorio, tener red vale mas que el ahorro.
            // Subject COMBINADO artista+obra: los MV oficiales titulan por el
            // artista (Tanya/MYTH & ROID, 17-jul) y los creditless por el ANIME
            // (Cat and Dragon, 18-jul) - con el subject solo artista, el
            // creditless oficial no matcheaba ni un token y la relevancia lo
            // tiraba. MentionsSubject acepta >=2 tokens.
            var themeSubject = $"{subject} {TrailerDownloadService.SubjectFromTitle(item.Title)}".Trim();
            logger.LogInformation("AnimeNews: buscando {Kind} en YouTube -> \"{Query}\" (obra: {Subject})",
                kind, query, themeSubject);
            candidates.AddRange(await trailerService.SearchManyAsync(
                query, requireSpanish: false, kind, themeSubject, ct: ct));

            // Escalera: si la query de la IA no encontro nada, probar la
            // heuristica (obra + palabra de tipo). Una query recargada de
            // serie+artista+cancion atrae videos fan que repiten todas esas
            // palabras y entierran al oficial (caso real 17-jul: Tanya the
            // Evil / MYTH & ROID - los 6 resultados eran mashups y covers).
            var heur = HeuristicVideoQuery(item.Title);
            if (candidates.Count == 0 && heur is not null && heur.Value.Query != query)
            {
                logger.LogInformation("AnimeNews: reintento con query heuristica -> \"{Query}\"", heur.Value.Query);
                candidates.AddRange(await trailerService.SearchManyAsync(
                    heur.Value.Query, requireSpanish: false, kind, themeSubject, ct: ct));
            }

            // Los cortos tienen dialogo: si hay subs es manuales, se queman. Solo
            // se buscan para el PRIMER candidato - bajarlos para todos serian N
            // llamadas de yt-dlp por un extra best-effort. Si el primero no se
            // puede bajar, el suplente va sin subs: es la noticia igual.
            if (candidates.Count > 0 && kind == NewsVideoKind.Short)
            {
                var shortSubs = await trailerService.DownloadSpanishSubtitlesAsync(candidates[0].Url, ct);
                if (shortSubs is not null) candidates[0] = candidates[0] with { SubtitlesPath = shortSubs };
            }
            return plan with { Candidates = Dedupe(candidates) };
        }

        // Trailer: cadena espanol -> cualquier idioma + subs es quemados
        logger.LogInformation("AnimeNews: buscando trailer en YouTube -> \"{Query}\" (obra: {Subject})",
            query, subject ?? "(derivada de la query)");

        // Los en espanol van primero en la lista: son los preferidos. Los de
        // otro idioma quedan detras como suplentes.
        var spanish = new List<TrailerCandidate>(
            await trailerService.SearchManyAsync(query, requireSpanish: true, subject: subject, ct: ct));

        // 2do intento: sin versión latina, el tráiler oficial en cualquier
        // idioma — con subtítulos es manuales quemados si existen, y si no, en
        // su idioma original (último recurso, toggle abajo). El PV embebido en
        // el artículo (rechazado antes por idioma) también entra acá.
        var anyQuery = StripSpanishSuffix(query);
        var any = new List<TrailerCandidate>(
            await trailerService.SearchManyAsync(anyQuery, requireSpanish: false, subject: subject, ct: ct));

        // Escalera: misma red de seguridad que en temas — si la query de la IA
        // no dio candidato en ninguno de los dos pasos, se prueba la heurística.
        if (spanish.Count == 0 && any.Count == 0)
        {
            var heur = HeuristicVideoQuery(item.Title);
            if (heur is not null && heur.Value.Kind == NewsVideoKind.Trailer && heur.Value.Query != query)
            {
                logger.LogInformation("AnimeNews: reintento con query heurística → \"{Query}\"", heur.Value.Query);
                spanish.AddRange(await trailerService.SearchManyAsync(
                    heur.Value.Query, requireSpanish: true, subject: subject, ct: ct));
                any.AddRange(await trailerService.SearchManyAsync(
                    StripSpanishSuffix(heur.Value.Query),
                    requireSpanish: false, subject: subject, ct: ct));
            }
        }
        if (any.Count == 0 && embeddedUrl is not null)
        {
            var embedded = await trailerService.ValidateAsync(
                embeddedUrl, requireSpanish: false, trustProvenance: true, ct: ct);
            if (embedded is not null) any.Add(embedded);
        }

        // Los que no estan en espanol llevan subtitulos es quemados si existen.
        // Solo se buscan para el primero de esa tanda: es el que mas chances
        // tiene de usarse y cada busqueda es una llamada de yt-dlp.
        if (any.Count > 0)
        {
            var subs = await trailerService.DownloadSpanishSubtitlesAsync(any[0].Url, ct);
            if (subs is not null)
            {
                logger.LogInformation("AnimeNews: trailer {Url} con subtitulos es para quemar", any[0].Url);
                any[0] = any[0] with { SubtitlesPath = subs };
            }
            else if (igSettings.TrailerOriginalLanguageFallback)
            {
                // Ultimo recurso (decision del usuario, jul-2026): el trailer
                // oficial va en su idioma original - el titular y las slides en
                // espanol encima dan el contexto, y el video correcto en japones
                // vale mas que un slideshow (la relevancia ya esta garantizada
                // por el gate de obra).
                logger.LogInformation(
                    "AnimeNews: trailer {Url} sin version latina ni subs es - va en idioma original", any[0].Url);
            }
            else
            {
                logger.LogInformation(
                    "AnimeNews: trailers sin version latina ni subtitulos manuales en espanol - descartados");
                any.Clear();
            }
        }

        return plan with { Candidates = Dedupe([.. spanish, .. any]) };
    }

    /// <summary>Candidatos sin URLs repetidas, respetando el orden de preferencia.</summary>
    private static IReadOnlyList<TrailerCandidate> Dedupe(IEnumerable<TrailerCandidate> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return [.. candidates.Where(c => seen.Add(c.Url))];
    }

    /// <summary>
    /// Resultado de la decisión de video: si la noticia AMERITA video
    /// (<see cref="WantsVideo"/>), la query/obra/tipo viajan al caller para que
    /// la escalera de respaldos (tweet del artículo → X vía IA → bilibili)
    /// corra aunque YouTube no haya dado candidato.
    /// </summary>
    private sealed record NewsVideoPlan(
        IReadOnlyList<TrailerCandidate> Candidates, bool WantsVideo, string Query, string? Subject, NewsVideoKind Kind)
    {
        public static readonly NewsVideoPlan None = new([], false, "", null, NewsVideoKind.Trailer);
    }

    private static NewsVideoKind ParseVideoKind(string? tipo) => tipo?.ToLowerInvariant() switch
    {
        "tema"  => NewsVideoKind.ThemeSong,
        "corto" => NewsVideoKind.Short,
        _       => NewsVideoKind.Trailer,
    };

    // Solo URLs de post de X/Twitter reales — la IA a veces devuelve links a
    // perfiles, búsquedas o YouTube; nada de eso sirve acá.
    private static readonly System.Text.RegularExpressions.Regex TweetUrlRegex =
        new(@"^https?://(www\.)?(x|twitter)\.com/[A-Za-z0-9_]+/status/\d+/?$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Busca en X (Twitter) el post de una cuenta OFICIAL con el video de la
    /// noticia — el respaldo cuando YouTube encontró el video pero la descarga
    /// está bloqueada desde CI (18-jul-2026: X no bloquea a los runners).
    /// Gemini CON GROUNDING encuentra la URL real (Gemma no tiene grounding:
    /// sin cuota de Gemini este respaldo no corre — best-effort); la URL se
    /// valida contra su metadata real en ValidateOfficialPostAsync antes de
    /// usarse. Devuelve el candidato o null (→ slideshow).
    /// </summary>
    private async Task<TrailerCandidate?> FindTweetVideoAsync(
        AnimeNewsItem item, NewsContent content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(aiSettings.ApiKey)) return null;

        GeminiResult? response = null;
        try
        {
            response = await gemini.GenerateDetailedAsync(
                "Buscá en X (Twitter) el post de una cuenta OFICIAL que contenga el VIDEO promocional " +
                "de esta noticia de anime (tráiler/teaser/opening/MV/corto). Cuentas válidas, en orden " +
                "de preferencia: @crunchyroll_la o @crunchyroll_es (versión en español), la cuenta " +
                "oficial de la obra o del juego/franquicia (los anuncios grandes SIEMPRE se publican " +
                "ahí — ej.: @Wuthering_Waves, @shingeki), la del artista (para MVs), su estudio o " +
                "distribuidor (Aniplex, TOHO animation, KADOKAWA…). Buscá con varios términos: nombre " +
                "de la obra + \"trailer\", + \"PV\", + \"anime\", en inglés y japonés. Tiene que ser un " +
                "post REAL y reciente con el video SUBIDO al post (no un link a YouTube); si la " +
                "búsqueda muestra la URL x.com/... o twitter.com/... del post, devolvé esa URL exacta. " +
                "Respondé SOLO un JSON: {\"url\": \"https://x.com/<cuenta>/status/<id>\"} " +
                "o {\"url\": null} si no encontrás ninguno.",
                $"Titular: {item.Title}\nResumen: {Truncate(item.Summary ?? content.Lede ?? string.Empty, 300)}",
                // groundingEssential: sin google_search esta llamada no busca
                // nada, inventa una URL de X. Con esto un 429 de grounding no
                // baja a Gemma (8 de 8 corridas de sep-2026 terminaban en "la IA
                // no encontró post de X usable", que ocultaba la causa real).
                useWebSearch: true, groundingEssential: true, ct: ct);

            using var doc = System.Text.Json.JsonDocument.Parse(GeminiClient.ExtractJsonObject(response.Text));
            var url = doc.RootElement.TryGetProperty("url", out var u)
                      && u.ValueKind == System.Text.Json.JsonValueKind.String
                ? u.GetString()
                : null;
            if (url is null || !TweetUrlRegex.IsMatch(url.Trim()))
            {
                logger.LogInformation("AnimeNews: la IA no encontró post de X usable ({Url})", url ?? "null");
                return null;
            }

            logger.LogInformation("AnimeNews: post de X candidato → {Url}", url.Trim());
            return await trailerService.ValidateOfficialPostAsync(
                url.Trim(), TrailerDownloadService.SubjectFromTitle(item.Title), ct: ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "AnimeNews: búsqueda del post de X falló. Respuesta: {Response}",
                response is null ? "(sin respuesta)" : Truncate(response.Text, 200));
            return null;
        }
    }

    /// <summary>
    /// Saca el sufijo de idioma ("español latino") de la query para la pasada
    /// en CUALQUIER idioma. Compara token a token SIN diacríticos y no contra
    /// un literal: el literal <c>"espanol latino"</c> (sin ñ) que dejó el
    /// PR #167 nunca matcheaba la query real —que la escriben el prompt de la
    /// IA y <see cref="HeuristicVideoQuery"/> CON ñ—, así que la pasada
    /// relajada corría la MISMA query en español y la escalera de idioma no
    /// existía. Entre el 24-ago y el 5-sep-2026 eso dejó sin video a las obras
    /// sin doblaje latino ("KochiKame: Tokyo Beat Cops tráiler oficial español
    /// latino" → 0 resultados, dos veces) y duplicó los requests a YouTube
    /// desde la misma IP de WARP. Público estático para tests.
    /// </summary>
    public static string StripSpanishSuffix(string query) =>
        string.Join(' ', query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => !SpanishSuffixWords.Contains(TrailerDownloadService.Normalize(w))));

    // Formas ya normalizadas (minúsculas sin diacríticos) — se comparan contra
    // el token normalizado, nunca contra el crudo.
    private static readonly HashSet<string> SpanishSuffixWords =
        new(StringComparer.Ordinal) { "espanol", "latino" };

    /// <summary>
    /// Fallback sin IA: solo busca cuando el titular anuncia material
    /// audiovisual, y clasifica el tipo (el orden importa: "estrena un corto"
    /// o "estrena opening" NO son noticias de tráiler aunque digan "estren").
    /// La query se arma con la OBRA del titular (SubjectFromTitle: palabras
    /// significativas SIN lo citado entre comillas — «lleno de magia» en la
    /// query dio literalmente 0 resultados en YouTube, caso real 16-jul) + el
    /// sufijo del tipo: el titular completo como query devolvía 0 resultados
    /// en prod (frases largas en español no matchean nada en YouTube). Para
    /// tráilers apunta a la versión doblada/subtitulada LATAM; para temas y
    /// cortos el idioma no aplica. Público estático para tests.
    /// </summary>
    public static (string Query, NewsVideoKind Kind)? HeuristicVideoQuery(string title)
    {
        // Sin diacríticos para detectar el tipo ("vídeo musical" = "video musical")
        var t = TrailerDownloadService.Normalize(title);
        var obra = TrailerDownloadService.SubjectFromTitle(title);
        if (obra.Length == 0) return null;

        if (ThemeWords.Any(t.Contains))
        {
            var themeWord = t.Contains("opening") ? "opening"
                          : t.Contains("ending") ? "ending"
                          : "mv";
            return ($"{obra} {themeWord}", NewsVideoKind.ThemeSong);
        }

        if (ShortFilmWords.Any(t.Contains)) return ($"{obra} special movie", NewsVideoKind.Short);

        return AudiovisualWords.Any(t.Contains)
            ? ($"{obra} tráiler oficial español latino", NewsVideoKind.Trailer)
            : null;
    }

    // Listas de detección de HeuristicVideoQuery. Se comparan contra `t`, que
    // sale de Normalize() (minúsculas SIN diacríticos), así que se normalizan
    // ACÁ en vez de confiar en cómo se escriba cada entrada: los literales
    // acentuados que había ("tráiler", "película", "adaptación") no matcheaban
    // nunca y solo sobrevivían por sus duplicados sin tilde.
    private static readonly string[] ThemeWords =
        Normalized("opening", "ending", "video musical", "tema musical");

    private static readonly string[] ShortFilmWords =
        Normalized("corto animado", "cortometraje", "video especial", "aniversario");

    // "estren" (raíz) cubre estreno/estrena/estrenará — "estrena un corto
    // animado" se caía por buscar el sustantivo exacto (bug real, jul-2026).
    // "live action" SIN guion va aparte del guionado: "The Apothecary Diaries
    // dará el salto al live action en 2028" salió sin video el 3-sep-2026
    // porque solo estaba la forma con guion.
    private static readonly string[] AudiovisualWords =
        Normalized("tráiler", "teaser", "avance", "temporada", "película",
                   "live-action", "live action", "adaptación", "estren");

    private static string[] Normalized(params string[] words) =>
        [.. words.Select(TrailerDownloadService.Normalize).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// ¿El titular anuncia material audiovisual (tráiler/opening/corto)? Las
    /// corridas de carrusel común lo usan para POSTERGAR esas noticias: sin
    /// esto, una noticia de opening caía en el slot de carrusel (sin video) en
    /// vez de esperar al próximo slot de reel. Público estático para tests.
    /// </summary>
    public static bool HasAudiovisualSignal(string title) => HeuristicVideoQuery(title) is not null;

    // ── Caption ──────────────────────────────────────────────────────────────

    // Instagram caption limit is 2200 characters.
    private const int IgCaptionMaxChars = 2200;

    // Always-on base hashtags. No "ñ" (Instagram mangles "#animeespañol") and no spaces.
    private static readonly string[] BaseHashtags =
        ["anime", "animelatino", "animenoticias", "manga", "otaku", "sheicobanime"];

    /// <summary>
    /// Gancho de compartir que abre el caption. Instagram corta el caption a
    /// ~125 caracteres: ese primer renglón es lo ÚNICO que se lee sin tocar
    /// "más". Hasta sep-2026 ahí iba "📰 {headline}" — exactamente el texto que
    /// ya está quemado en el video/cover, o sea que el espacio más valioso del
    /// post se gastaba en repetir lo que el usuario acababa de leer.
    ///
    /// Ahora abre pidiendo el share, que es el mecanismo de distribución medido:
    /// los 48 reels con ≥10 shares (16 % del output) concentran el 62 % de todas
    /// las views, y los que tienen al menos un repost hacen 6,9× la mediana del
    /// resto. El titular no se pierde: sigue en el cover y en las slides.
    /// </summary>
    private static readonly string[] ShareHooks =
    [
        "Mandale esto a quien lo estaba esperando 👇",
        "Etiquetá a quien tiene que ver esto 👇",
        "Compartilo con quien sigue esta serie 👇",
        "¿A quién le mandarías esta noticia? 👇",
        "Guardalo y mandáselo a tu grupo otaku 👇",
    ];

    /// <summary>
    /// Elige el gancho de forma estable a partir del titular — variedad a lo
    /// largo del feed sin depender de <c>string.GetHashCode</c>, que .NET
    /// aleatoriza por proceso (dos corridas darían ganchos distintos para la
    /// misma noticia y el test no sería reproducible). Público para tests.
    /// </summary>
    public static string PickShareHook(string seed)
    {
        var sum = 0;
        foreach (var c in seed) sum = (sum + c) % 4096;
        return ShareHooks[sum % ShareHooks.Length];
    }

    /// <summary>
    /// Builds the Instagram caption from the already-rewritten content: el gancho
    /// de compartir, the original editorial body (the rewrite — never the source
    /// text; ahora más largo/profundo que las slides), a CTA, smart hashtags and
    /// the handle. El cuerpo se presupuesta para que los hashtags y el @ nunca
    /// queden fuera del límite de IG (2200). La música no lleva línea en el
    /// caption: el crédito CC va como texto chico dentro del video.
    /// </summary>
    private string BuildCaption(NewsContent content)
    {
        var header = $"{PickShareHook(content.Headline)}\n\n";

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
    private async Task<(IReadOnlyList<string> Images, string? TrailerUrl, string? TweetUrl)> GatherMediaAsync(
        AnimeNewsItem item, CancellationToken ct)
    {
        var images = new List<string>();
        string? trailerUrl = null;
        string? tweetUrl = null;
        if (!string.IsNullOrWhiteSpace(item.ImageUrl)) images.Add(item.ImageUrl!);
        if (string.IsNullOrWhiteSpace(item.ArticleUrl))
            return (images, null, null);

        try
        {
            var http = httpFactory.CreateClient("news-rss");
            using var resp = await http.GetAsync(item.ArticleUrl, ct);
            if (resp.IsSuccessStatusCode)
            {
                var html = await resp.Content.ReadAsStringAsync(ct);
                images.AddRange(AnimeNewsFeedService.ExtractArticleImages(html));
                trailerUrl = AnimeNewsFeedService.ExtractArticleVideoUrl(html);
                tweetUrl = AnimeNewsFeedService.ExtractArticleTweetUrl(html);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "AnimeNews: could not gather extra media for {Title}", Truncate(item.Title, 50));
        }

        return (images.Distinct().Take(6).ToList(), trailerUrl, tweetUrl);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string SlideFileName(AnimeNewsItem item, int index) =>
        $"news-{item.SourceKey}-{item.Id.ToString("N")[..8]}-{index}.jpg";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
