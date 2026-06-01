using AnimeIndex.Api.Data.Entities;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>
/// Generates news-format Story (1080×1920) and Feed (1080×1080) images using SkiaSharp.
///
/// Layout (story):
///   • Article photo fills top 60% (cover-fill crop, dark gradient over it)
///   • "📰 NOTICIAS" badge at ~62%
///   • Article title (bold white, 2-3 lines, word-wrap)
///   • Summary snippet (small, light gray, 2 lines max)
///   • Source attribution + SheicobAnime logo
///
/// For feed (square): same layout but image fills top 45%, text region taller.
/// </summary>
public class AnimeNewsImageService(
    IHttpClientFactory httpFactory,
    ILogger<AnimeNewsImageService> logger)
{
    // Brand palette — matches existing episode image style
    private static readonly SKColor BgTop      = new(0x18, 0x00, 0x38);   // deep purple
    private static readonly SKColor BgBottom   = new(0x08, 0x08, 0x08);   // near-black
    private static readonly SKColor Accent     = new(0xFF, 0x6B, 0x35);   // orange
    private static readonly SKColor AccentDark = new(0xCC, 0x44, 0x00);   // darker orange for badge border
    private static readonly SKColor TextWhite  = new(0xFF, 0xFF, 0xFF);
    private static readonly SKColor TextGray   = new(0xBB, 0xBB, 0xBB);
    private static readonly SKColor BadgeBg    = new(0xFF, 0x6B, 0x35, 0xEE);

    // Shared logo (same as episode image service)
    private static readonly Lazy<SKBitmap?> Logo = new(() =>
    {
        var asm    = typeof(AnimeNewsImageService).Assembly;
        var stream = asm.GetManifestResourceStream("AnimeIndex.Scraper.Resources.sheicob-logo.png");
        return stream is null ? null : SKBitmap.Decode(stream);
    });

    public async Task<byte[]> GenerateStoryAsync(AnimeNewsItem item, string sourceDisplayName, CancellationToken ct = default)
        => await GenerateAsync(item, sourceDisplayName, 1080, 1920, isStory: true, ct);

    public async Task<byte[]> GenerateFeedAsync(AnimeNewsItem item, string sourceDisplayName, CancellationToken ct = default)
        => await GenerateAsync(item, sourceDisplayName, 1080, 1080, isStory: false, ct);

    private async Task<byte[]> GenerateAsync(
        AnimeNewsItem item, string sourceDisplayName,
        int width, int height, bool isStory, CancellationToken ct)
    {
        SKBitmap? photoBitmap = null;

        if (!string.IsNullOrWhiteSpace(item.ImageUrl))
        {
            try
            {
                var http  = httpFactory.CreateClient("probe");
                var bytes = await http.GetByteArrayAsync(item.ImageUrl, ct);
                photoBitmap = SKBitmap.Decode(bytes);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "AnimeNews: could not download image from {Url}", item.ImageUrl);
            }
        }

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        DrawBackground(canvas, width, height);

        if (photoBitmap is not null)
        {
            using (photoBitmap)
                DrawPhoto(canvas, photoBitmap, width, height, isStory);
        }

        DrawGradientOverlay(canvas, width, height, isStory);
        DrawTextContent(canvas, item, sourceDisplayName, width, height, isStory);

        using var image = surface.Snapshot();
        using var data  = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }

    // ── Drawing helpers ──────────────────────────────────────────────────────

    private static void DrawBackground(SKCanvas canvas, int width, int height)
    {
        using var paint  = new SKPaint();
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(0, height),
            [BgTop, BgBottom], null, SKShaderTileMode.Clamp);
        paint.Shader = shader;
        canvas.DrawRect(0, 0, width, height, paint);
    }

    private static void DrawPhoto(
        SKCanvas canvas, SKBitmap photo, int width, int height, bool isStory)
    {
        float destH = height * (isStory ? 0.60f : 0.45f);
        var destRect = new SKRect(0, 0, width, destH);

        // Cover-fill crop
        float srcAspect = (float)photo.Width / photo.Height;
        float dstAspect = (float)width / destH;
        SKRect srcRect;
        if (srcAspect > dstAspect)
        {
            float cropW  = photo.Height * dstAspect;
            float offsetX = (photo.Width - cropW) / 2f;
            srcRect = new SKRect(offsetX, 0, offsetX + cropW, photo.Height);
        }
        else
        {
            float cropH  = photo.Width / dstAspect;
            float offsetY = (photo.Height - cropH) / 2f;
            srcRect = new SKRect(0, offsetY, photo.Width, offsetY + cropH);
        }

        using var img   = SKImage.FromBitmap(photo);
        using var paint = new SKPaint();
        canvas.DrawImage(img, srcRect, destRect,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
    }

    private static void DrawGradientOverlay(SKCanvas canvas, int width, int height, bool isStory)
    {
        // Main gradient: transparent top → opaque bottom
        float gradStart = isStory ? 0.40f : 0.28f;
        using var paint  = new SKPaint();
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, height * gradStart), new SKPoint(0, height),
            [SKColors.Transparent, new SKColor(0x08, 0x08, 0x08, 0xF8)],
            null, SKShaderTileMode.Clamp);
        paint.Shader = shader;
        canvas.DrawRect(0, 0, width, height, paint);

        // Vignette
        using var vPaint  = new SKPaint();
        using var vShader = SKShader.CreateRadialGradient(
            new SKPoint(width / 2f, height / 2f),
            Math.Max(width, height) * 0.72f,
            [SKColors.Transparent, new SKColor(0, 0, 0, 70)],
            null, SKShaderTileMode.Clamp);
        vPaint.Shader = vShader;
        canvas.DrawRect(0, 0, width, height, vPaint);
    }

    private static void DrawTextContent(
        SKCanvas canvas, AnimeNewsItem item, string sourceDisplayName,
        int width, int height, bool isStory)
    {
        float scale = width / 1080f;
        float x     = width * 0.07f;

        // ── "NOTICIAS" badge ─────────────────────────────────────────────
        float badgeY = isStory ? height * 0.635f : height * 0.57f;
        DrawBadge(canvas, "  📰  NOTICIAS  ", x, badgeY, scale);

        // ── Headline ─────────────────────────────────────────────────────
        float titleY    = badgeY + 18 * scale;
        float titleSize = isStory ? 62 * scale : 54 * scale;
        float afterTitle = DrawWrappedText(
            canvas, item.Title, x, titleY, titleSize,
            TextWhite, maxWidth: width * 0.86f, bold: true, maxLines: 3);

        // ── Summary (2 lines max) ─────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(item.Summary))
        {
            float summarySize = 30 * scale;
            float summaryY    = afterTitle + 10 * scale;
            DrawWrappedText(
                canvas, item.Summary, x, summaryY, summarySize,
                TextGray, maxWidth: width * 0.86f, bold: false, maxLines: 2);
        }

        // ── Source attribution ────────────────────────────────────────────
        float sourceY    = isStory ? height * 0.915f : height * 0.885f;
        float sourceSize = 24 * scale;
        var sourceText   = $"Fuente: {sourceDisplayName}";
        DrawText(canvas, sourceText, x, sourceY, sourceSize, new SKColor(0x99, 0x99, 0x99));

        // ── Branding mark (bottom-right) ──────────────────────────────────
        float brandY = height - 36 * scale;
        DrawBrandingMark(canvas, width, brandY, scale);
    }

    private static void DrawBadge(SKCanvas canvas, string text, float x, float y, float scale)
    {
        using var font      = CreateFont(22 * scale, bold: true);
        using var textPaint = new SKPaint { Color = TextWhite, IsAntialias = true };
        float textWidth = font.MeasureText(text);
        float padH = 16 * scale, padV = 8 * scale, radius = 5 * scale;

        using var bgPaint = new SKPaint { Color = BadgeBg, IsAntialias = true };
        var bgRect = new SKRoundRect(
            new SKRect(x - padH, y - font.Size - padV, x + textWidth + padH, y + padV),
            radius);
        canvas.DrawRoundRect(bgRect, bgPaint);
        canvas.DrawText(text, x, y, font, textPaint);
    }

    private static void DrawText(SKCanvas canvas, string text, float x, float y,
        float fontSize, SKColor color, bool bold = false)
    {
        using var font        = CreateFont(fontSize, bold);
        using var paint       = new SKPaint { Color = color, IsAntialias = true };
        using var shadowPaint = new SKPaint { Color = new SKColor(0, 0, 0, 150), IsAntialias = true };
        canvas.DrawText(text, x + 2, y + 2, font, shadowPaint);
        canvas.DrawText(text, x, y, font, paint);
    }

    private static float DrawWrappedText(
        SKCanvas canvas, string text, float x, float y,
        float fontSize, SKColor color, float maxWidth, bool bold, int maxLines)
    {
        using var font        = CreateFont(fontSize, bold);
        using var paint       = new SKPaint { Color = color, IsAntialias = true };
        using var shadowPaint = new SKPaint { Color = new SKColor(0, 0, 0, 150), IsAntialias = true };

        var lines      = WrapText(text, font, maxWidth, maxLines);
        float lineH    = fontSize * 1.28f;

        foreach (var line in lines)
        {
            canvas.DrawText(line, x + 2, y + 2, font, shadowPaint);
            canvas.DrawText(line, x, y, font, paint);
            y += lineH;
        }
        return y;
    }

    private static void DrawBrandingMark(SKCanvas canvas, int width, float y, float scale)
    {
        var logo = Logo.Value;
        if (logo is not null)
        {
            float logoW = 170 * scale;
            float logoH = logo.Height * (logoW / logo.Width);
            float lx    = width - logoW - 20 * scale;
            var dest    = new SKRect(lx, y - logoH, lx + logoW, y);
            using var p = new SKPaint { IsAntialias = true };
            canvas.DrawBitmap(logo, dest, p);
        }
        else
        {
            string brand     = "SheicobAnime";
            float fontSize   = 24 * scale;
            using var font   = CreateFont(fontSize, bold: true);
            using var paint  = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0xAA), IsAntialias = true };
            float tw = font.MeasureText(brand);
            canvas.DrawText(brand, width - tw - 34 * scale, y, font, paint);
        }
    }

    // ── Text utilities ───────────────────────────────────────────────────────

    private static SKFont CreateFont(float size, bool bold)
    {
        var weight   = bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        var typeface = SKTypeface.FromFamilyName(
            "sans-serif",
            new SKFontStyle(weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright))
            ?? SKTypeface.Default;
        return new SKFont(typeface, size);
    }

    private static List<string> WrapText(string text, SKFont font, float maxWidth, int maxLines)
    {
        var words   = text.Split(' ');
        var lines   = new List<string>();
        var current = "";

        foreach (var word in words)
        {
            if (lines.Count >= maxLines) break;
            var test = current.Length == 0 ? word : current + " " + word;
            if (font.MeasureText(test) > maxWidth && current.Length > 0)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = test;
            }
        }
        if (current.Length > 0 && lines.Count < maxLines)
        {
            // Truncate last line with ellipsis if we hit the max
            if (lines.Count == maxLines - 1 && font.MeasureText(current) > maxWidth)
            {
                while (current.Length > 0 && font.MeasureText(current + "…") > maxWidth)
                    current = current[..^1];
                lines.Add(current + "…");
            }
            else
            {
                lines.Add(current);
            }
        }
        return lines;
    }
}
