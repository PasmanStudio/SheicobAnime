using AnimeIndex.Api.Data.Entities;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>
/// Generates Story (1080x1920) and Feed (1080x1080) images using SkiaSharp.
/// Downloads the anime cover art and composites it with branded overlays.
/// </summary>
public class InstagramImageService(
    IHttpClientFactory httpClientFactory,
    ILogger<InstagramImageService> logger)
{
    // Brand colours
    private static readonly SKColor BgTop     = new(0x18, 0x00, 0x38);  // deep purple
    private static readonly SKColor BgBottom  = new(0x08, 0x08, 0x08);  // near-black
    private static readonly SKColor Accent    = new(0xFF, 0x6B, 0x35);  // orange
    private static readonly SKColor TextWhite = new(0xFF, 0xFF, 0xFF);
    private static readonly SKColor BadgeBg   = new(0xFF, 0x6B, 0x35, 0xEE);

    // Logo loaded once from embedded resource; null = file missing (uses text fallback)
    private static readonly Lazy<SKBitmap?> Logo = new(() =>
    {
        var asm    = typeof(InstagramImageService).Assembly;
        var stream = asm.GetManifestResourceStream("AnimeIndex.Scraper.Resources.sheicob-logo.png");
        return stream is null ? null : SKBitmap.Decode(stream);
    });

    public async Task<byte[]> GenerateStoryAsync(
        Series series, Episode episode, CancellationToken ct = default)
        => await GenerateAsync(series, episode, 1080, 1920, isStory: true, ct);

    public async Task<byte[]> GenerateFeedAsync(
        Series series, Episode episode, CancellationToken ct = default)
        => await GenerateAsync(series, episode, 1080, 1080, isStory: false, ct);

    private async Task<byte[]> GenerateAsync(
        Series series, Episode episode, int width, int height, bool isStory, CancellationToken ct)
    {
        SKBitmap? coverBitmap = null;
        var imageUrl = episode.ThumbnailUrl ?? series.CoverUrl;

        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            try
            {
                var http = httpClientFactory.CreateClient("probe");
                var bytes = await http.GetByteArrayAsync(imageUrl, ct);
                coverBitmap = SKBitmap.Decode(bytes);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not download cover image from {Url} — using gradient fallback", imageUrl);
            }
        }

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        DrawBackground(canvas, width, height);

        if (coverBitmap is not null)
        {
            using (coverBitmap)
                DrawCoverImage(canvas, coverBitmap, width, height, isStory);
        }

        DrawGradientOverlay(canvas, width, height, isStory);
        DrawTextContent(canvas, series, episode, width, height, isStory);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }

    private static void DrawBackground(SKCanvas canvas, int width, int height)
    {
        using var paint = new SKPaint();
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0),
            new SKPoint(0, height),
            [BgTop, BgBottom],
            null,
            SKShaderTileMode.Clamp);
        paint.Shader = shader;
        canvas.DrawRect(0, 0, width, height, paint);
    }

    private static void DrawCoverImage(
        SKCanvas canvas, SKBitmap cover, int width, int height, bool isStory)
    {
        // Cover occupies top 68% for story, full height for feed (with stronger overlay)
        var destHeight = isStory ? (int)(height * 0.68f) : height;
        var destRect = new SKRect(0, 0, width, destHeight);

        // Maintain aspect ratio (cover fill — crop excess)
        float srcAspect = (float)cover.Width / cover.Height;
        float dstAspect = (float)width / destHeight;
        SKRect srcRect;
        if (srcAspect > dstAspect)
        {
            // Source wider than dest — crop sides
            float cropW = cover.Height * dstAspect;
            float offsetX = (cover.Width - cropW) / 2f;
            srcRect = new SKRect(offsetX, 0, offsetX + cropW, cover.Height);
        }
        else
        {
            // Source taller than dest — crop top/bottom
            float cropH = cover.Width / dstAspect;
            float offsetY = (cover.Height - cropH) / 2f;
            srcRect = new SKRect(0, offsetY, cover.Width, offsetY + cropH);
        }

        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
        canvas.DrawBitmap(cover, srcRect, destRect, paint);
    }

    private static void DrawGradientOverlay(SKCanvas canvas, int width, int height, bool isStory)
    {
        // Gradient over entire height — transparent at top, opaque at bottom
        float gradientStart = isStory ? 0.45f : 0.30f;
        using var paint = new SKPaint();
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, height * gradientStart),
            new SKPoint(0, height),
            [SKColors.Transparent, new SKColor(0x08, 0x08, 0x08, 0xF5)],
            null,
            SKShaderTileMode.Clamp);
        paint.Shader = shader;
        canvas.DrawRect(0, 0, width, height, paint);

        // Thin vignette on all edges for polish
        using var vignette = new SKPaint();
        using var vignetteShader = SKShader.CreateRadialGradient(
            new SKPoint(width / 2f, height / 2f),
            Math.Max(width, height) * 0.7f,
            [SKColors.Transparent, new SKColor(0, 0, 0, 80)],
            null,
            SKShaderTileMode.Clamp);
        vignette.Shader = vignetteShader;
        canvas.DrawRect(0, 0, width, height, vignette);
    }

    private static void DrawTextContent(
        SKCanvas canvas, Series series, Episode episode, int width, int height, bool isStory)
    {
        float scale = width / 1080f;

        // ── "NUEVO EPISODIO" badge ──────────────────────────────────
        float badgeY = isStory ? height * 0.71f : height * 0.64f;
        DrawBadge(canvas, "NUEVO EPISODIO", width * 0.07f, badgeY, scale);

        // ── Series title ─────────────────────────────────────────────
        float titleY = isStory ? height * 0.775f : height * 0.72f;
        float titleFontSize = isStory ? 68 * scale : 60 * scale;
        float afterTitleY = DrawText(canvas, series.Title, width * 0.07f, titleY,
            titleFontSize, TextWhite, maxWidth: width * 0.86f, bold: true);

        // ── Episode label — flows directly below the title ────────────
        float epFontSize = 42 * scale;
        float epY = afterTitleY + 6 * scale;
        var epLabel = episode.Title is { Length: > 0 } t
            ? $"Episodio {episode.EpisodeNumber} · {TruncateAt(t, 32)}"
            : $"Episodio {episode.EpisodeNumber}";
        float afterEpY = DrawText(canvas, epLabel, width * 0.07f, epY, epFontSize, Accent);

        // ── CTA line — at least a small gap below the episode label ──
        float ctaDefaultY = isStory ? height * 0.91f : height * 0.875f;
        float ctaY = Math.Max(ctaDefaultY, afterEpY + 12 * scale);
        DrawText(canvas, "Miralo en @sheicobanime  ·  Link en bio",
            width * 0.07f, ctaY, 28 * scale, new SKColor(0xDD, 0xDD, 0xDD));

        // ── Branding watermark (bottom-right) ────────────────────────
        float brandY = height - 40 * scale;
        DrawBrandingMark(canvas, width, brandY, scale);
    }

    private static void DrawBadge(SKCanvas canvas, string text, float x, float y, float scale)
    {
        using var font = CreateFont(22 * scale, bold: true);
        using var textPaint = new SKPaint { Color = TextWhite, IsAntialias = true };
        float textWidth = font.MeasureText(text);
        float padH = 18 * scale;
        float padV = 10 * scale;
        float radius = 6 * scale;

        using var bgPaint = new SKPaint { Color = BadgeBg, IsAntialias = true };
        var bgRect = new SKRoundRect(
            new SKRect(x - padH, y - font.Size - padV, x + textWidth + padH, y + padV),
            radius);
        canvas.DrawRoundRect(bgRect, bgPaint);

        canvas.DrawText(text, x, y, font, textPaint);
    }

    private static float DrawText(
        SKCanvas canvas, string text, float x, float y,
        float fontSize, SKColor color, float maxWidth = 0, bool bold = false)
    {
        using var font       = CreateFont(fontSize, bold);
        using var paint      = new SKPaint { Color = color, IsAntialias = true };
        using var shadowPaint = new SKPaint { Color = new SKColor(0, 0, 0, 160), IsAntialias = true };

        var lines      = maxWidth > 0 ? WrapText(text, font, maxWidth) : [text];
        float lineHeight = fontSize * 1.25f;

        foreach (var line in lines)
        {
            canvas.DrawText(line, x + 2, y + 2, font, shadowPaint);
            canvas.DrawText(line, x, y, font, paint);
            y += lineHeight;
        }
        return y;
    }

    private static void DrawBrandingMark(SKCanvas canvas, int width, float y, float scale)
    {
        var logo = Logo.Value;
        if (logo is not null)
        {
            float logoW = 180 * scale;
            float logoH = logo.Height * (logoW / logo.Width);
            float x = width - logoW - 20 * scale;
            var dest = new SKRect(x, y - logoH, x + logoW, y);
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawBitmap(logo, dest, paint);
        }
        else
        {
            string brand = "SheicobAnime";
            float fontSize = 26 * scale;
            using var font  = CreateFont(fontSize, bold: true);
            using var paint = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0xBB), IsAntialias = true };
            float textWidth = font.MeasureText(brand);
            canvas.DrawText(brand, width - textWidth - 36 * scale, y, font, paint);
        }
    }

    private static SKFont CreateFont(float size, bool bold = false)
    {
        var weight = bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        var typeface = SKTypeface.FromFamilyName(
            "sans-serif",
            new SKFontStyle(weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright))
            ?? SKTypeface.Default;
        return new SKFont(typeface, size);
    }

    private static List<string> WrapText(string text, SKFont font, float maxWidth)
    {
        var words = text.Split(' ');
        var lines = new List<string>();
        var current = "";

        foreach (var word in words)
        {
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
        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    private static string TruncateAt(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen].TrimEnd() + "…";
}
