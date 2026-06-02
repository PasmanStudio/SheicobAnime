using AnimeIndex.Api.Data.Entities;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>
/// Generates "poster"-style news images with SkiaSharp, inspired by editorial anime-news
/// accounts (koryugi, animeshowcast): each slide is a striking image + a big condensed
/// headline + minimal supporting text. The full article body goes in the caption, NOT on
/// the slides.
///
///   • Cover (slide 1)   : article photo full-bleed + dark scrim + "NOTICIAS" kicker
///                         + big Anton title (uppercase) + one-line lede + "Desliza →".
///   • Key-point slides  : brand background + one big Anton headline (the first sentence
///                         of a body paragraph) — a single punchy idea per slide.
///   • Story (9:16)      : the cover layout in portrait (with a link sticker added later).
///
/// Headlines use the bundled Anton font (Resources/Anton-Regular.ttf), falling back to a
/// bold system sans if the font resource is missing.
/// </summary>
public class AnimeNewsImageService(
    IHttpClientFactory httpFactory,
    ILogger<AnimeNewsImageService> logger)
{
    // ── Brand palette ──────────────────────────────────────────────────────────
    private static readonly SKColor BgTop     = new(0x18, 0x00, 0x38);   // deep purple
    private static readonly SKColor BgBottom  = new(0x08, 0x08, 0x08);   // near-black
    private static readonly SKColor Accent    = new(0xFF, 0x6B, 0x35);   // orange
    private static readonly SKColor TextWhite = new(0xFF, 0xFF, 0xFF);
    private static readonly SKColor TextGray  = new(0xCC, 0xCC, 0xCC);
    private static readonly SKColor Scrim     = new(0x05, 0x02, 0x10);   // scrim base

    private static readonly Lazy<SKBitmap?> Logo =
        new(() => LoadBitmap("AnimeIndex.Scraper.Resources.sheicob-logo.png"));
    private static readonly Lazy<SKTypeface?> Anton =
        new(() => LoadTypeface("AnimeIndex.Scraper.Resources.Anton-Regular.ttf"));

    // ── Public API ─────────────────────────────────────────────────────────────

    public async Task<byte[]> GenerateStoryAsync(AnimeNewsItem item, CancellationToken ct = default)
    {
        var photo = await TryDownloadPhotoAsync(item.ImageUrl, ct);
        try { return RenderCover(item, photo, 1080, 1920, swipeHint: false); }
        finally { photo?.Dispose(); }
    }

    public async Task<byte[]> GenerateFeedAsync(AnimeNewsItem item, CancellationToken ct = default)
    {
        var photo = await TryDownloadPhotoAsync(item.ImageUrl, ct);
        try { return RenderCover(item, photo, 1080, 1080, swipeHint: false); }
        finally { photo?.Dispose(); }
    }

    /// <summary>
    /// Builds the carousel: a cover poster followed by up to <paramref name="maxKeyPoints"/>
    /// headline slides (one short idea each). Falls back to just the cover when the item has
    /// no usable body text.
    /// </summary>
    public async Task<List<byte[]>> GenerateCarouselSlidesAsync(
        AnimeNewsItem item, int maxKeyPoints = 2, CancellationToken ct = default)
    {
        var photo = await TryDownloadPhotoAsync(item.ImageUrl, ct);
        try
        {
            var headlines = ExtractKeyPoints(item.Summary, maxKeyPoints);
            int total = headlines.Count + 1;

            var slides = new List<byte[]>(total)
            {
                RenderCover(item, photo, 1080, 1080, swipeHint: headlines.Count > 0),
            };
            for (int i = 0; i < headlines.Count; i++)
                slides.Add(RenderKeyPoint(headlines[i], pageNumber: i + 2, totalPages: total));

            return slides;
        }
        finally { photo?.Dispose(); }
    }

    // ── Cover poster ───────────────────────────────────────────────────────────

    private static byte[] RenderCover(AnimeNewsItem item, SKBitmap? photo, int width, int height, bool swipeHint)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        DrawBackground(canvas, width, height);
        if (photo is not null) DrawPhotoCover(canvas, photo, width, height);
        DrawBottomScrim(canvas, width, height);

        float scale  = width / 1080f;
        float x      = width * 0.07f;
        float bottom = height - height * 0.07f;

        DrawBrandingMark(canvas, width, bottom, scale);

        float y = bottom;

        // Swipe CTA (lowest element) — only on the carousel cover.
        if (swipeHint)
        {
            DrawText(canvas, "DESLIZA PARA LEER  →", x, y, 26 * scale, Accent, display: false, bold: true);
            y -= 58 * scale;
        }

        // One-line lede (subtitle).
        var lede = FirstSentence(item.Summary, 105);
        if (!string.IsNullOrWhiteSpace(lede))
        {
            float subSize = 29 * scale, subLineH = 38 * scale;
            var subLines = Wrap(lede!, subSize, width * 0.86f, bold: false, display: false, maxLines: 2);
            y = DrawBlockBottomUp(canvas, subLines, x, y, subSize, TextGray, subLineH, display: false, bold: false)
                - 18 * scale;
        }

        // Big condensed title (Anton, uppercase) — grows upward from the lede.
        float titleSize  = (height > width ? 104 : 86) * scale;
        float titleLineH = titleSize * 1.06f;
        var titleLines = Wrap(item.Title.ToUpperInvariant(), titleSize, width * 0.86f, bold: true, display: true, maxLines: 4);
        y = DrawBlockBottomUp(canvas, titleLines, x, y, titleSize, TextWhite, titleLineH, display: true, bold: true)
            - 22 * scale;

        // Kicker: accent bar + "NOTICIAS".
        DrawKicker(canvas, x, y, scale);

        return Encode(surface);
    }

    // ── Key-point slide (one big headline, brand background) ─────────────────────

    private static byte[] RenderKeyPoint(string headline, int pageNumber, int totalPages)
    {
        const int width = 1080, height = 1080;
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        DrawBackground(canvas, width, height);
        DrawCenterGlow(canvas, width, height);

        float scale = width / 1080f;
        float x     = width * 0.08f;

        // Kicker (top-left) + page number (top-right).
        float topY = height * 0.16f;
        DrawKicker(canvas, x, topY, scale);
        DrawTextRight(canvas, $"{pageNumber}/{totalPages}", width - x, topY, 26 * scale, TextGray, bold: true);

        // Big headline (Anton, uppercase), vertically centered.
        float hlSize  = 84 * scale;
        float hlLineH = hlSize * 1.06f;
        var lines  = Wrap(headline.ToUpperInvariant(), hlSize, width * 0.84f, bold: true, display: true, maxLines: 6);
        float blockH = lines.Count * hlLineH;
        float firstBaseline = (height - blockH) / 2f + hlSize * 0.82f;
        DrawLines(canvas, lines, x, firstBaseline, hlSize, TextWhite, hlLineH, display: true, bold: true);

        // Accent bar under the headline block.
        float barY = firstBaseline + (lines.Count - 1) * hlLineH + 34 * scale;
        using (var bar = new SKPaint { Color = Accent, IsAntialias = true })
            canvas.DrawRect(x, barY, 90 * scale, 8 * scale, bar);

        // Continuation arrow (non-final) + branding.
        float brandY = height - height * 0.07f;
        if (pageNumber < totalPages)
            DrawText(canvas, "→", x, brandY, 54 * scale, Accent, display: false, bold: true);
        DrawBrandingMark(canvas, width, brandY, scale);

        return Encode(surface);
    }

    // ── Shared chrome ────────────────────────────────────────────────────────────

    private static void DrawKicker(SKCanvas canvas, float x, float baselineY, float scale)
    {
        using (var bar = new SKPaint { Color = Accent, IsAntialias = true })
            canvas.DrawRect(x, baselineY - 22 * scale, 64 * scale, 7 * scale, bar);
        DrawText(canvas, "NOTICIAS", x + 80 * scale, baselineY, 27 * scale, TextWhite, display: false, bold: true);
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

    private static void DrawPhotoCover(SKCanvas canvas, SKBitmap photo, int width, int height)
    {
        var dest = new SKRect(0, 0, width, height);
        float srcAspect = (float)photo.Width / photo.Height;
        float dstAspect = (float)width / height;
        SKRect src;
        if (srcAspect > dstAspect)
        {
            float cropW = photo.Height * dstAspect;
            float offX  = (photo.Width - cropW) / 2f;
            src = new SKRect(offX, 0, offX + cropW, photo.Height);
        }
        else
        {
            float cropH = photo.Width / dstAspect;
            float offY  = (photo.Height - cropH) / 2f;
            src = new SKRect(0, offY, photo.Width, offY + cropH);
        }
        using var img   = SKImage.FromBitmap(photo);
        using var paint = new SKPaint();
        canvas.DrawImage(img, src, dest,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
    }

    /// <summary>Cinematic dark gradient so bottom-anchored text always reads over the photo.</summary>
    private static void DrawBottomScrim(SKCanvas canvas, int width, int height)
    {
        using var paint  = new SKPaint();
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, height * 0.28f), new SKPoint(0, height),
            [SKColors.Transparent, Scrim.WithAlpha(0xC8), Scrim.WithAlpha(0xFB)],
            [0f, 0.62f, 1f], SKShaderTileMode.Clamp);
        paint.Shader = shader;
        canvas.DrawRect(0, 0, width, height, paint);
    }

    /// <summary>Soft radial highlight behind a key-point headline.</summary>
    private static void DrawCenterGlow(SKCanvas canvas, int width, int height)
    {
        using var paint  = new SKPaint();
        using var shader = SKShader.CreateRadialGradient(
            new SKPoint(width / 2f, height / 2f), width * 0.7f,
            [new SKColor(0x2A, 0x10, 0x55, 0x80), SKColors.Transparent],
            null, SKShaderTileMode.Clamp);
        paint.Shader = shader;
        canvas.DrawRect(0, 0, width, height, paint);
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
            DrawTextRight(canvas, "SheicobAnime", width - 34 * scale, y, 26 * scale,
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

    private static void DrawTextRight(SKCanvas canvas, string text, float rightX, float y,
        float size, SKColor color, bool bold)
    {
        using var font = CreateFont(size, bold, display: false);
        float w = font.MeasureText(text);
        DrawText(canvas, text, rightX - w, y, size, color, display: false, bold: bold);
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

    private static List<string> Wrap(string text, float size, float maxWidth, bool bold, bool display, int maxLines)
    {
        using var font = CreateFont(size, bold, display);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var cur   = "";
        var truncated = false;

        foreach (var w in words)
        {
            var test = cur.Length == 0 ? w : cur + " " + w;
            if (cur.Length == 0 || font.MeasureText(test) <= maxWidth)
            {
                cur = test;
            }
            else
            {
                lines.Add(cur);
                cur = w;
                if (lines.Count == maxLines) { truncated = true; break; }
            }
        }
        if (!truncated && cur.Length > 0) lines.Add(cur);

        if (truncated && lines.Count > 0)
        {
            var last = lines[^1];
            while (last.Length > 0 && font.MeasureText(last + "…") > maxWidth)
                last = last[..^1].TrimEnd();
            lines[^1] = last + "…";
        }
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

    // ── Headline extraction (heuristic) ──────────────────────────────────────────

    /// <summary>
    /// Picks up to <paramref name="max"/> short headlines from the body — the first sentence
    /// of each paragraph. Paragraph 0 (the lede) is reserved for the cover subtitle, so we
    /// start at paragraph 1 (unless there is only one paragraph).
    /// </summary>
    private static List<string> ExtractKeyPoints(string? summary, int max)
    {
        if (string.IsNullOrWhiteSpace(summary) || max <= 0) return [];

        var paras = summary
            .Split(["\n\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        if (paras.Count == 0) return [];

        int start = paras.Count > 1 ? 1 : 0;
        var result = new List<string>();
        for (int i = start; i < paras.Count && result.Count < max; i++)
        {
            var s = FirstSentence(paras[i], 140);
            if (!string.IsNullOrWhiteSpace(s) && s!.Length >= 20)
                result.Add(s!);
        }
        return result;
    }

    /// <summary>First sentence of the text, trimmed to <paramref name="maxLen"/> at a clause/word boundary.</summary>
    private static string? FirstSentence(string? text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var firstPara = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(firstPara)) return null;

        int end = firstPara.IndexOfAny(['.', '!', '?']);
        var s = end > 0 ? firstPara[..(end + 1)] : firstPara;

        if (s.Length > maxLen)
        {
            var slice = s[..maxLen];
            int comma = slice.LastIndexOf(',');
            int space = slice.LastIndexOf(' ');
            s = (comma > maxLen / 2 ? slice[..comma]
                : space > 0 ? slice[..space]
                : slice).TrimEnd() + "…";
        }
        return s;
    }

    // ── Photo download (forces a SkiaSharp-decodable format) ─────────────────────

    private async Task<SKBitmap?> TryDownloadPhotoAsync(string? imageUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return null;

        // Some CDNs (e.g. SomosKudasai) serve AVIF via "format=auto", which SkiaSharp cannot
        // decode — leaving a photo-less cover. Force a decodable format. No-op for URLs that
        // don't contain "format=auto" (og:image links, etc.).
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
