using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Scraper.Infrastructure.AiRewrite;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>
/// Generates "poster"-style news images with SkiaSharp, aligned with the site's
/// "Abismo + Neón" brand (abismo background with a blue cast — never pure black — cian accent,
/// the signature 14° cut, heavy condensed display headlines). Editorial, koryugi-style:
/// every slide is a striking image + one big headline.
///
///   • Cover (slide 1)      : main photo full-bleed + abismo scrim + cian "NOTICIAS" cut-kicker
///                            + big auto-fit headline + one-line lede.
///   • Key-point slides      : a photo (cycling through the article's images, with varied crops)
///                            + scrim + one original key point. Falls back to an abismo panel
///                            with a center glow when there are no photos.
///   • Closing slide         : branded CTA ("Seguí leyendo · Link en bio").
///   • Story (9:16)          : the cover layout in portrait (link sticker added later).
///
/// Text never gets cut with "…": headlines auto-shrink to fit (<see cref="WrapFit"/>).
/// All text comes from the already-rewritten <see cref="NewsContent"/> — never the raw summary —
/// so no source chrome or copyright can reach a slide.
/// </summary>
public class AnimeNewsImageService(
    IHttpClientFactory httpFactory,
    ILogger<AnimeNewsImageService> logger)
{
    // ── Brand palette — "Abismo + Neón" ─────────────────────────────────────────
    private static readonly SKColor BgTop     = new(0x0B, 0x14, 0x22);   // abismo, blue cast (top)
    private static readonly SKColor BgBottom  = new(0x07, 0x09, 0x0E);   // abismo base (bottom)
    private static readonly SKColor Accent    = new(0x14, 0xB1, 0xE7);   // cian (único acento)
    private static readonly SKColor TextWhite = new(0xF4, 0xF8, 0xFC);   // ink
    private static readonly SKColor TextGray  = new(0x9F, 0xB0, 0xC0);   // ink muted
    private static readonly SKColor Scrim     = new(0x04, 0x06, 0x0A);   // scrim base
    private const float CutSlant = 0.25f;                                // tan(14°) ≈ 0.25 → the 14° cut

    private enum CropFocus { Center, Top, Bottom, Left, Right }

    // ── Zona segura de Instagram (piezas verticales) ────────────────────────────
    // En Reels IG dibuja SU PROPIA UI encima del video: arriba el header con el
    // ícono de cámara (~250 px de 1920) y abajo el caption, el usuario, el ticker
    // de audio y la botonera de la derecha (~420 px). Y en la grilla del perfil el
    // cover se recorta a 4:5, que se come otros ~285 px arriba y abajo.
    //
    // Hasta sep-2026 el titular se anclaba al 7 % del borde inferior — y≈1786 de
    // 1920 — así que el bloque titular+lede vivía casi entero DEBAJO de la
    // botonera, y en la grilla directamente no aparecía. Estas constantes lo
    // corren a la banda central, que sobrevive tanto a la UI del reel como al
    // recorte 4:5. Las piezas cuadradas (carrusel de feed) no tienen chrome
    // encima y conservan su margen chico.
    //
    // Los dos valores son un poco más conservadores que el mínimo de cada
    // restricción, y por motivos distintos:
    //   • Arriba manda el recorte 4:5 (285 px), que es MÁS exigente que el
    //     header del reel (250 px) — el logo del cover tiene que sobrevivir a
    //     los dos, así que se toma el mayor.
    //   • Abajo el valor es la última LÍNEA DE BASE utilizable, no el borde del
    //     texto: los glifos con descendente (g, j, p, y) bajan por debajo de la
    //     base, así que se reservan ~20 px extra sobre los 420 de la UI.
    private const float PortraitSafeTop    = 0.15f;   // ≈288/1920 (recorte 4:5 > header)
    private const float PortraitSafeBottom = 0.23f;   // ≈442/1920 (420 de UI + descendentes)

    /// <summary>Última línea de base utilizable: la zona segura en vertical,
    /// <paramref name="squareMargin"/> en cuadrado (donde no hay UI encima).</summary>
    private static float SafeBottom(int width, int height, float squareMargin) =>
        height - height * (height > width ? PortraitSafeBottom : squareMargin);

    /// <summary>Techo utilizable: la zona segura en vertical, 0 en cuadrado.</summary>
    private static float SafeTop(int width, int height) =>
        height > width ? height * PortraitSafeTop : 0f;

    /// <summary>Tope de un bloque de <paramref name="blockH"/> px centrado en la
    /// banda segura (no en el canvas entero: en 9:16 el centro real queda bajo
    /// el caption de IG).</summary>
    private static float CenteredBaseline(int width, int height, float blockH, float squareMargin)
    {
        float top    = SafeTop(width, height);
        float bottom = SafeBottom(width, height, squareMargin);
        return top + (bottom - top - blockH) / 2f;
    }

    private static readonly Lazy<SKBitmap?> Logo =
        new(() => LoadBitmap("AnimeIndex.Scraper.Resources.sheicob-logo.png"));
    private static readonly Lazy<SKTypeface?> Anton =
        new(() => LoadTypeface("AnimeIndex.Scraper.Resources.Anton-Regular.ttf"));

    // ── Public API ─────────────────────────────────────────────────────────────

    public async Task<byte[]> GenerateStoryAsync(
        AnimeNewsItem item, NewsContent content, IReadOnlyList<string> imageUrls, CancellationToken ct = default)
    {
        using var photo = await TryDownloadFirstUsableAsync(BuildImageList(item, imageUrls), ct);
        return RenderCover(content, photo, 1080, 1920, swipeHint: false);
    }

    /// <summary>
    /// Slides 9:16 (1080×1920) para el Reel-slideshow de noticias: cover +
    /// hasta <paramref name="maxKeyPoints"/> puntos clave + CTA de cierre —
    /// el mismo contenido editorial del carrusel pero en formato vertical.
    /// ffmpeg las encadena con Ken Burns alternado y crossfades.
    /// <paramref name="musicCredit"/> (tracks CC) va como texto chico en la
    /// slide de cierre — la atribución vive en el video, no en el caption.
    /// </summary>
    public async Task<List<byte[]>> GenerateReelSlidesAsync(
        AnimeNewsItem item, NewsContent content, IReadOnlyList<string> imageUrls,
        int maxKeyPoints = 3, string? musicCredit = null, CancellationToken ct = default)
    {
        const int width = 1080, height = 1920;
        var urls   = BuildImageList(item, imageUrls);
        var photos = await DownloadPhotosAsync(urls, ct);
        try
        {
            var keyPoints = content.KeyPoints.Take(Math.Max(0, maxKeyPoints)).ToList();

            var slides = new List<byte[]>(keyPoints.Count + 2)
            {
                RenderCover(content, photos.Count > 0 ? photos[0] : null, width, height, swipeHint: false),
            };

            for (int i = 0; i < keyPoints.Count; i++)
            {
                // Mismo cycling de fotos/encuadres que el carrusel
                var photo = photos.Count > 0 ? photos[(i + 1) % photos.Count] : null;
                var focus = (CropFocus)(i % 5);
                slides.Add(RenderKeyPoint(keyPoints[i], photo, focus, width, height));
            }

            slides.Add(RenderCta(width, height, musicCredit));
            return slides;
        }
        finally { foreach (var p in photos) p.Dispose(); }
    }

    /// <summary>
    /// Capas para el Reel "tráiler + titular": fondo abismo SIN foto (el video
    /// es el protagonista — glow + scrim para que el texto respire) y el mismo
    /// overlay de texto de marca. El tráiler va como banda central encima del
    /// fondo, el texto encima de todo.
    /// </summary>
    public (byte[] Background, byte[] OverlayPng) GenerateVideoReelLayers(
        NewsContent content, string? musicCredit = null)
    {
        const int width = 1080, height = 1920;

        using var bgSurface = SKSurface.Create(new SKImageInfo(width, height));
        var bg = bgSurface.Canvas;
        bg.Clear(SKColors.Black);
        DrawBackground(bg, width, height);
        DrawCenterGlow(bg, width, height);
        DrawBottomScrim(bg, width, height);

        using var ovSurface = SKSurface.Create(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var ov = ovSurface.Canvas;
        ov.Clear(SKColors.Transparent);
        DrawCoverText(ov, content, width, height, swipeHint: false);
        if (musicCredit is not null) DrawMusicCredit(ov, musicCredit, width, height);

        return (Encode(bgSurface), EncodePng(ovSurface));
    }

    /// <summary>
    /// Capas separadas del cover 9:16 para el Reel de noticias (motion graphics):
    /// fondo (abismo + foto + scrim, JPEG) y overlay (titular/lede/kicker/logo
    /// sobre transparente, PNG con alpha). ffmpeg las anima por separado — Ken
    /// Burns en la foto, slide-in con fade en el texto, que queda siempre nítido.
    /// </summary>
    public async Task<(byte[] Background, byte[] OverlayPng)> GenerateStoryLayersAsync(
        AnimeNewsItem item, NewsContent content, IReadOnlyList<string> imageUrls,
        string? musicCredit = null, CancellationToken ct = default)
    {
        const int width = 1080, height = 1920;
        using var photo = await TryDownloadFirstUsableAsync(BuildImageList(item, imageUrls), ct);

        // ── Fondo ──
        using var bgSurface = SKSurface.Create(new SKImageInfo(width, height));
        var bg = bgSurface.Canvas;
        bg.Clear(SKColors.Black);
        DrawBackground(bg, width, height);
        if (photo is not null) DrawPhotoCover(bg, photo, width, height, CropFocus.Center);
        DrawBottomScrim(bg, width, height);

        // ── Overlay de texto (transparente) ──
        using var ovSurface = SKSurface.Create(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var ov = ovSurface.Canvas;
        ov.Clear(SKColors.Transparent);
        DrawCoverText(ov, content, width, height, swipeHint: false);
        if (musicCredit is not null) DrawMusicCredit(ov, musicCredit, width, height);

        return (Encode(bgSurface), EncodePng(ovSurface));
    }

    /// <summary>
    /// Builds the carousel: cover → up to <paramref name="maxKeyPoints"/> key-point slides
    /// (each over a photo, cycling through the article's images with varied crops) → a closing CTA.
    /// Instagram caps carousels at 10; we stay well under.
    /// </summary>
    public async Task<List<byte[]>> GenerateCarouselSlidesAsync(
        AnimeNewsItem item, NewsContent content, IReadOnlyList<string> imageUrls,
        int maxKeyPoints = 5, CancellationToken ct = default)
    {
        var urls   = BuildImageList(item, imageUrls);
        var photos = await DownloadPhotosAsync(urls, ct);
        try
        {
            var keyPoints = content.KeyPoints.Take(Math.Max(0, maxKeyPoints)).ToList();

            var slides = new List<byte[]>(keyPoints.Count + 2)
            {
                RenderCover(content, photos.Count > 0 ? photos[0] : null, 1080, 1080,
                    swipeHint: keyPoints.Count > 0),
            };

            for (int i = 0; i < keyPoints.Count; i++)
            {
                // Cycle images so each slide has a photo; vary the crop so reused images differ.
                var photo = photos.Count > 0 ? photos[(i + 1) % photos.Count] : null;
                var focus = (CropFocus)(i % 5);
                slides.Add(RenderKeyPoint(keyPoints[i], photo, focus));
            }

            slides.Add(RenderCta());
            return slides;
        }
        finally { foreach (var p in photos) p.Dispose(); }
    }

    // ── Cover poster ───────────────────────────────────────────────────────────

    private static byte[] RenderCover(NewsContent content, SKBitmap? photo, int width, int height, bool swipeHint)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        DrawBackground(canvas, width, height);
        if (photo is not null) DrawPhotoCover(canvas, photo, width, height, CropFocus.Center);
        DrawBottomScrim(canvas, width, height);

        DrawCoverText(canvas, content, width, height, swipeHint);

        return Encode(surface);
    }

    /// <summary>
    /// Todo el texto/branding del cover (logo, kicker, titular, lede, swipe hint).
    /// Separado del fondo para que el Reel lo anime como capa independiente.
    /// </summary>
    private static void DrawCoverText(SKCanvas canvas, NewsContent content, int width, int height, bool swipeHint)
    {
        float scale  = width / 1080f;
        float x      = width * 0.07f;
        float bottom = SafeBottom(width, height, 0.07f);

        // Logo lives at the TOP on the cover so it never sits over the bottom-anchored text.
        DrawLogoTop(canvas, width, height, scale);

        float y = bottom;

        if (swipeHint)
        {
            DrawMono(canvas, "DESLIZÁ PARA LEER  →", x, y, 25 * scale, Accent, bold: true);
            y -= 58 * scale;
        }

        var lede = string.IsNullOrWhiteSpace(content.Lede) ? null : content.Lede!.Trim();
        if (!string.IsNullOrWhiteSpace(lede))
        {
            var (subLines, subSize) = WrapFit(lede!, 30 * scale, 23 * scale, width * 0.86f, maxLines: 3, bold: false, display: false);
            float subLineH = subSize * 1.3f;
            y = DrawBlockBottomUp(canvas, subLines, x, y, subSize, TextGray, subLineH, display: false, bold: false)
                - 18 * scale;
        }

        // Big condensed title (Anton, uppercase), auto-fit so it never truncates.
        var title = string.IsNullOrWhiteSpace(content.Headline) ? "" : content.Headline.Trim();
        var (titleLines, titleSize) = WrapFit(title.ToUpperInvariant(),
            (height > width ? 104 : 92) * scale, 50 * scale, width * 0.86f, maxLines: 5, bold: true, display: true);
        float titleLineH = titleSize * 1.06f;
        y = DrawBlockBottomUp(canvas, titleLines, x, y, titleSize, TextWhite, titleLineH, display: true, bold: true)
            - 22 * scale;

        DrawKicker(canvas, x, y, scale, "NOTICIAS");
    }

    // ── Key-point slide (photo + one big headline; abismo panel as fallback) ─────

    private static byte[] RenderKeyPoint(string headline, SKBitmap? photo, CropFocus focus,
        int width = 1080, int height = 1080)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        float scale = width / 1080f;
        float x     = width * 0.08f;
        bool hasPhoto = photo is not null;

        DrawBackground(canvas, width, height);
        if (hasPhoto)
        {
            DrawPhotoCover(canvas, photo!, width, height, focus);
            DrawBottomScrim(canvas, width, height);
        }
        else
        {
            DrawCenterGlow(canvas, width, height);
        }

        // Kicker (top-left) only — no page numbers. En 9:16 baja a la zona
        // segura para no quedar bajo el header del reel.
        float topY = MathF.Max(height * 0.13f, SafeTop(width, height));
        DrawKicker(canvas, x, topY, scale, "SHEICOBANIME");

        var (lines, hlSize) = WrapFit(headline.ToUpperInvariant(),
            84 * scale, 46 * scale, width * 0.84f, maxLines: 7, bold: true, display: true);
        float hlLineH = hlSize * 1.06f;

        if (hasPhoto)
        {
            // Bottom-anchored over the scrim (editorial, like the cover) — pero
            // sobre la zona segura, no sobre el borde del canvas.
            float bottom = SafeBottom(width, height, 0.10f);
            float firstBaseline = bottom - (lines.Count - 1) * hlLineH;
            DrawLines(canvas, lines, x, firstBaseline, hlSize, TextWhite, hlLineH, display: true, bold: true);
            DrawCut(canvas, x, firstBaseline - hlSize - 30 * scale, 96 * scale, 12 * scale, Accent);
        }
        else
        {
            // Centrado en la BANDA SEGURA del panel abismo (no en el canvas
            // entero): en 9:16 el centro real cae debajo del caption de IG.
            float blockH = lines.Count * hlLineH;
            float firstBaseline = CenteredBaseline(width, height, blockH, 0.10f) + hlSize * 0.82f;
            DrawLines(canvas, lines, x, firstBaseline, hlSize, TextWhite, hlLineH, display: true, bold: true);
            float barY = firstBaseline + (lines.Count - 1) * hlLineH + 30 * scale;
            DrawCut(canvas, x, barY, 96 * scale, 12 * scale, Accent);
        }

        // No logo here on purpose — the "SHEICOBANIME" kicker already brands the slide,
        // so nothing overlaps the bottom-anchored headline.
        return Encode(surface);
    }

    // ── Closing CTA slide ────────────────────────────────────────────────────────

    private static byte[] RenderCta(int width = 1080, int height = 1080, string? musicCredit = null)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        DrawBackground(canvas, width, height);
        DrawCenterGlow(canvas, width, height);

        float scale = width / 1080f;
        float x     = width * 0.08f;

        DrawKicker(canvas, x, MathF.Max(height * 0.13f, SafeTop(width, height)), scale, "SHEICOBANIME");

        var (lines, size) = WrapFit("SEGUÍ LA NOTA COMPLETA", 96 * scale, 60 * scale, width * 0.84f,
            maxLines: 3, bold: true, display: true);
        float lineH = size * 1.06f;
        float blockH = lines.Count * lineH;
        float firstBaseline = CenteredBaseline(width, height, blockH, 0.07f) + size * 0.6f;
        DrawLines(canvas, lines, x, firstBaseline, size, TextWhite, lineH, display: true, bold: true);

        float barY = firstBaseline + (lines.Count - 1) * lineH + 30 * scale;
        DrawCut(canvas, x, barY, 110 * scale, 13 * scale, Accent);
        DrawMono(canvas, "LINK EN LA BIO  →", x, barY + 70 * scale, 30 * scale, Accent, bold: true);

        // El logo se sube a la zona segura, y deja lugar arriba del crédito CC
        // cuando hay uno (que ahora también vive dentro de la zona segura).
        float mark = SafeBottom(width, height, 0.07f) - (musicCredit is null ? 0 : 46 * scale);
        DrawBrandingMark(canvas, width, mark, scale);
        if (musicCredit is not null) DrawMusicCredit(canvas, musicCredit, width, height);
        return Encode(surface);
    }

    /// <summary>
    /// Crédito de la música como texto chico y discreto al pie de la zona segura
    /// — la atribución CC BY es obligatoria, así que tiene que quedar VISIBLE:
    /// pegado al borde del canvas (donde estaba) lo tapaba el caption de IG en
    /// los reels. Vive EN el video (estilo créditos de cierre), nunca en el caption.
    /// </summary>
    private static void DrawMusicCredit(SKCanvas canvas, string credit, int width, int height)
    {
        float scale    = width / 1080f;
        float x        = width * 0.08f;
        float maxWidth = width * 0.84f;
        float size     = 20 * scale;

        using (var probe = CreateMonoFont(size, bold: false))
        {
            var w = probe.MeasureText(credit);
            if (w > maxWidth) size *= maxWidth / w;   // que nunca se salga del canvas
        }

        DrawMono(canvas, credit, x, SafeBottom(width, height, 0.024f), size,
            TextGray.WithAlpha(0xB4), bold: false);
    }

    // ── Shared chrome ────────────────────────────────────────────────────────────

    private static void DrawKicker(SKCanvas canvas, float x, float baselineY, float scale, string label)
    {
        DrawCut(canvas, x, baselineY - 24 * scale, 60 * scale, 14 * scale, Accent);
        DrawMono(canvas, label, x + 84 * scale, baselineY, 26 * scale, TextWhite, bold: true);
    }

    /// <summary>Draws the signature 14° cut (a -14° skewed bar) at (x, y).</summary>
    private static void DrawCut(SKCanvas canvas, float x, float y, float w, float h, SKColor color)
    {
        float slant = h * CutSlant * 4f;
        using var path = new SKPath();
        path.MoveTo(x + slant, y);
        path.LineTo(x + slant + w, y);
        path.LineTo(x + w, y + h);
        path.LineTo(x, y + h);
        path.Close();
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawPath(path, paint);
    }

    private static void DrawBackground(SKCanvas canvas, int width, int height)
    {
        using var paint  = new SKPaint();
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(0, height),
            [BgTop, BgBottom], null, SKShaderTileMode.Clamp);
        paint.Shader = shader;
        canvas.DrawRect(0, 0, width, height, paint);
    }

    private static void DrawPhotoCover(SKCanvas canvas, SKBitmap photo, int width, int height, CropFocus focus)
    {
        float srcAspect = (float)photo.Width / photo.Height;
        float dstAspect = (float)width / height;
        SKRect src;
        if (srcAspect > dstAspect)
        {
            float cropW  = photo.Height * dstAspect;
            float maxOff = photo.Width - cropW;
            float offX   = focus switch
            {
                CropFocus.Left  => maxOff * 0.18f,
                CropFocus.Right => maxOff * 0.82f,
                _               => maxOff * 0.5f,
            };
            src = new SKRect(offX, 0, offX + cropW, photo.Height);
        }
        else
        {
            float cropH  = photo.Width / dstAspect;
            float maxOff = photo.Height - cropH;
            float offY   = focus switch
            {
                CropFocus.Top    => maxOff * 0.12f,
                CropFocus.Bottom => maxOff * 0.88f,
                _                => maxOff * 0.5f,
            };
            src = new SKRect(0, offY, photo.Width, offY + cropH);
        }

        // Zoom in ~4% so any watermark baked into the source's edges is pushed off-canvas.
        float insetX = src.Width  * 0.04f;
        float insetY = src.Height * 0.04f;
        src = new SKRect(src.Left + insetX, src.Top + insetY, src.Right - insetX, src.Bottom - insetY);

        using var img   = SKImage.FromBitmap(photo);
        using var paint = new SKPaint();
        canvas.DrawImage(img, src, new SKRect(0, 0, width, height),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
    }

    /// <summary>Cinematic abismo gradient so bottom-anchored text always reads over the photo.</summary>
    private static void DrawBottomScrim(SKCanvas canvas, int width, int height)
    {
        using var paint  = new SKPaint();
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, height * 0.24f), new SKPoint(0, height),
            [SKColors.Transparent, Scrim.WithAlpha(0xCC), Scrim.WithAlpha(0xFC)],
            [0f, 0.58f, 1f], SKShaderTileMode.Clamp);
        paint.Shader = shader;
        canvas.DrawRect(0, 0, width, height, paint);
    }

    private static void DrawCenterGlow(SKCanvas canvas, int width, int height)
    {
        using var paint  = new SKPaint();
        using var shader = SKShader.CreateRadialGradient(
            new SKPoint(width / 2f, height / 2f), width * 0.7f,
            [new SKColor(0x14, 0xB1, 0xE7, 0x33), SKColors.Transparent],
            null, SKShaderTileMode.Clamp);
        paint.Shader = shader;
        canvas.DrawRect(0, 0, width, height, paint);
    }

    /// <summary>Logo in the top-right corner. En 9:16 baja hasta la zona segura:
    /// el chrome de IG vive justo ahí arriba a la derecha — el ícono de cámara en
    /// Reels, el avatar y el "..." en stories — y a 150 px el logo quedaba
    /// tapado. Se usa en el cover, donde el texto se ancla abajo.</summary>
    private static void DrawLogoTop(SKCanvas canvas, int width, int height, float scale)
    {
        bool isStory = height > width;
        float topY   = isStory ? SafeTop(width, height) : 52f * scale;
        var logo = Logo.Value;
        if (logo is not null)
        {
            float logoW = 165 * scale;
            float logoH = logo.Height * (logoW / logo.Width);
            float lx    = width - logoW - 40 * scale;
            var dest    = new SKRect(lx, topY, lx + logoW, topY + logoH);
            using var p = new SKPaint { IsAntialias = true };
            canvas.DrawBitmap(logo, dest, p);
        }
        else
        {
            DrawMonoRight(canvas, "SheicobAnime", width - 40 * scale, topY + 22 * scale, 24 * scale,
                new SKColor(0xFF, 0xFF, 0xFF, 0xCC), bold: true);
        }
    }

    private static void DrawBrandingMark(SKCanvas canvas, int width, float y, float scale)
    {
        var logo = Logo.Value;
        if (logo is not null)
        {
            float logoW = 175 * scale;
            float logoH = logo.Height * (logoW / logo.Width);
            float lx    = width - logoW - 20 * scale;
            var dest    = new SKRect(lx, y - logoH, lx + logoW, y);
            using var p = new SKPaint { IsAntialias = true };
            canvas.DrawBitmap(logo, dest, p);
        }
        else
        {
            DrawMonoRight(canvas, "SheicobAnime", width - 34 * scale, y, 26 * scale,
                new SKColor(0xFF, 0xFF, 0xFF, 0xAA), bold: true);
        }
    }

    // ── Text helpers ─────────────────────────────────────────────────────────────

    private static void DrawText(SKCanvas canvas, string text, float x, float y,
        float size, SKColor color, bool display, bool bold)
    {
        using var font   = CreateFont(size, bold, display);
        using var paint  = new SKPaint { Color = color, IsAntialias = true };
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 170), IsAntialias = true };
        canvas.DrawText(text, x + 2, y + 2, font, shadow);
        canvas.DrawText(text, x, y, font, paint);
    }

    private static void DrawMono(SKCanvas canvas, string text, float x, float y,
        float size, SKColor color, bool bold)
    {
        using var font   = CreateMonoFont(size, bold);
        using var paint  = new SKPaint { Color = color, IsAntialias = true };
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 150), IsAntialias = true };
        canvas.DrawText(text, x + 2, y + 2, font, shadow);
        canvas.DrawText(text, x, y, font, paint);
    }

    private static void DrawMonoRight(SKCanvas canvas, string text, float rightX, float y,
        float size, SKColor color, bool bold)
    {
        using var font = CreateMonoFont(size, bold);
        float w = font.MeasureText(text);
        DrawMono(canvas, text, rightX - w, y, size, color, bold);
    }

    private static void DrawLines(SKCanvas canvas, List<string> lines, float x, float firstBaseline,
        float size, SKColor color, float lineH, bool display, bool bold)
    {
        using var font   = CreateFont(size, bold, display);
        using var paint  = new SKPaint { Color = color, IsAntialias = true };
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 170), IsAntialias = true };
        float y = firstBaseline;
        foreach (var line in lines)
        {
            canvas.DrawText(line, x + 2, y + 3, font, shadow);
            canvas.DrawText(line, x, y, font, paint);
            y += lineH;
        }
    }

    /// <summary>Draws a block so its LAST baseline sits at <paramref name="lastBaseline"/>;
    /// returns the baseline for content placed directly above it.</summary>
    private static float DrawBlockBottomUp(SKCanvas canvas, List<string> lines, float x, float lastBaseline,
        float size, SKColor color, float lineH, bool display, bool bold)
    {
        float firstBaseline = lastBaseline - (lines.Count - 1) * lineH;
        DrawLines(canvas, lines, x, firstBaseline, size, color, lineH, display, bold);
        return firstBaseline - lineH;
    }

    /// <summary>
    /// Wraps text to <paramref name="maxWidth"/> and shrinks the font from <paramref name="maxSize"/>
    /// down to <paramref name="minSize"/> until it fits in <paramref name="maxLines"/> lines — so
    /// headlines are never cut with "…". Returns the chosen lines and font size.
    /// </summary>
    private static (List<string> lines, float size) WrapFit(
        string text, float maxSize, float minSize, float maxWidth, int maxLines, bool bold, bool display)
    {
        var step = MathF.Max(2f, maxSize * 0.06f);
        List<string> lines = [];
        for (float size = maxSize; size >= minSize; size -= step)
        {
            lines = WrapNoTruncate(text, size, maxWidth, bold, display);
            if (lines.Count <= maxLines) return (lines, size);
        }
        // Still too tall at minSize (extreme): keep min size, hard-cap line count.
        lines = WrapNoTruncate(text, minSize, maxWidth, bold, display);
        if (lines.Count > maxLines) lines = lines.Take(maxLines).ToList();
        return (lines, minSize);
    }

    private static List<string> WrapNoTruncate(string text, float size, float maxWidth, bool bold, bool display)
    {
        using var font = CreateFont(size, bold, display);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var cur   = "";
        foreach (var w in words)
        {
            var test = cur.Length == 0 ? w : cur + " " + w;
            if (cur.Length == 0 || font.MeasureText(test) <= maxWidth) cur = test;
            else { lines.Add(cur); cur = w; }
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }

    private static SKFont CreateFont(float size, bool bold, bool display)
    {
        if (display && Anton.Value is { } anton)
            return new SKFont(anton, size);

        var weight   = bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        var typeface = SKTypeface.FromFamilyName(
            "sans-serif",
            new SKFontStyle(weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright))
            ?? SKTypeface.Default;
        return new SKFont(typeface, size);
    }

    private static SKFont CreateMonoFont(float size, bool bold)
    {
        var weight   = bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        var typeface = SKTypeface.FromFamilyName(
            "monospace",
            new SKFontStyle(weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright))
            ?? SKTypeface.Default;
        return new SKFont(typeface, size);
    }

    // ── Photo download ───────────────────────────────────────────────────────────

    // Gate de calidad para fondos: una imagen chica estirada a 1080px se
    // pixela y arruina la slide (pasó con logos/banners de la página fuente).
    // Mejor SIN foto (panel abismo de marca) que con una foto fea.
    internal const int MinPhotoWidth = 500;
    internal const int MinPhotoHeight = 350;
    private const float MaxPhotoAspectRatio = 3.2f;   // banners ultra anchos/altos

    /// <summary>True si la imagen aguanta ser fondo de slide sin pixelarse ni
    /// ser un banner. Público estático para tests.</summary>
    public static bool IsUsablePhoto(SKBitmap photo) =>
        photo.Width >= MinPhotoWidth && photo.Height >= MinPhotoHeight
        && photo.Width <= photo.Height * MaxPhotoAspectRatio
        && photo.Height <= photo.Width * MaxPhotoAspectRatio;

    /// <summary>Primary image first, then any extras, deduped — capped at 6.</summary>
    private static List<string> BuildImageList(AnimeNewsItem item, IReadOnlyList<string> imageUrls)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.ImageUrl)) list.Add(item.ImageUrl!);
        list.AddRange(imageUrls.Where(u => !string.IsNullOrWhiteSpace(u)));
        return list.Distinct().Take(6).ToList();
    }

    /// <summary>
    /// True if at least one of the URLs downloads AND decodes. The publisher uses this to
    /// guarantee every post has a real image — we never publish a text-only/flat poster.
    /// </summary>
    public async Task<bool> HasDecodableImageAsync(IReadOnlyList<string> urls, CancellationToken ct = default)
    {
        foreach (var url in urls)
        {
            var bmp = await TryDownloadPhotoAsync(url, ct);
            if (bmp is not null) { bmp.Dispose(); return true; }
        }
        return false;
    }

    private async Task<List<SKBitmap>> DownloadPhotosAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        var photos = new List<SKBitmap>();
        foreach (var url in urls)
        {
            var bmp = await TryDownloadPhotoAsync(url, ct);
            if (bmp is null) continue;
            if (!IsUsablePhoto(bmp))
            {
                logger.LogDebug("AnimeNews: descarto imagen de baja calidad ({W}×{H}) de {Url}",
                    bmp.Width, bmp.Height, url);
                bmp.Dispose();
                continue;
            }
            photos.Add(bmp);
        }
        return photos;
    }

    /// <summary>Primera foto de la lista que pasa el gate de calidad, o null
    /// (el cover cae al panel abismo — mejor que una foto pixelada).</summary>
    private async Task<SKBitmap?> TryDownloadFirstUsableAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        foreach (var url in urls)
        {
            var bmp = await TryDownloadPhotoAsync(url, ct);
            if (bmp is null) continue;
            if (IsUsablePhoto(bmp)) return bmp;
            bmp.Dispose();
        }
        return null;
    }

    private async Task<SKBitmap?> TryDownloadPhotoAsync(string? imageUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return null;

        // Some CDNs (e.g. SomosKudasai) serve AVIF via "format=auto", which SkiaSharp cannot
        // decode. Force a decodable format. No-op for URLs without "format=auto".
        var url = imageUrl.Replace("format=auto", "format=jpeg", StringComparison.OrdinalIgnoreCase);

        try
        {
            var http  = httpFactory.CreateClient("probe");
            var bytes = await http.GetByteArrayAsync(url, ct);
            var bmp   = SKBitmap.Decode(bytes);
            if (bmp is null)
                logger.LogDebug("AnimeNews: could not decode image (unsupported format?) from {Url}", url);
            return bmp;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "AnimeNews: could not download image from {Url}", url);
            return null;
        }
    }

    // ── Resource loading ─────────────────────────────────────────────────────────

    private static byte[] EncodePng(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] Encode(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var data  = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }

    private static SKBitmap? LoadBitmap(string resourceName)
    {
        using var stream = typeof(AnimeNewsImageService).Assembly.GetManifestResourceStream(resourceName);
        return stream is null ? null : SKBitmap.Decode(stream);
    }

    private static SKTypeface? LoadTypeface(string resourceName)
    {
        using var stream = typeof(AnimeNewsImageService).Assembly.GetManifestResourceStream(resourceName);
        return stream is null ? null : SKTypeface.FromStream(stream);
    }
}
