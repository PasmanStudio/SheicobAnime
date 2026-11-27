using SkiaSharp;

namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>
/// Cuadros 1920x1080 generados para lo que no tiene una foto: tarjetas de datos
/// (premio, cifra, fecha) y paneles de logos. Fondo: arte de la serie
/// desenfocado y oscurecido; el texto queda entre y=170 y y=880 para no chocar
/// con los subtítulos (abajo) ni perderse con el acercamiento de la toma.
/// </summary>
public static class CardRenderer
{
    public const int W = 1920, H = 1080;
    private static readonly SKColor Accent = new(0x14, 0xB1, 0xE7);
    private static readonly SKColor Accent2 = new(0xFF, 0x2B, 0xD6);
    private static readonly SKColor Ink = new(0x0B, 0x0B, 0x14);

    private static readonly Lazy<SKTypeface> Display = new(() =>
    {
        using var s = typeof(CardRenderer).Assembly.GetManifestResourceStream("AnimeIndex.Scraper.Resources.Anton-Regular.ttf");
        return (s is null ? null : SKTypeface.FromStream(s)) ?? SKTypeface.Default;
    });

    private static readonly Lazy<SKTypeface> Body = new(() =>
        SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        ?? SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold)
        ?? SKTypeface.Default);

    /// <summary>Tarjeta de dato: título grande, subtítulo y, si hay, la imagen que lo acompaña a la izquierda.</summary>
    public static void RenderCard(string title, string? detail, string? imagePath, string? backgroundPath, string output)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var canvas = surface.Canvas;
        DrawBackground(canvas, backgroundPath ?? imagePath);

        using var image = imagePath is null ? null : SKBitmap.Decode(imagePath);
        float textLeft = 200, textRight = W - 200;
        if (image is not null)
        {
            var h = 700f;
            var w = Math.Min(620f, h * image.Width / image.Height);
            h = w * image.Height / image.Width;
            var rect = SKRect.Create(190, (H - h) / 2 - 20, w, h);
            DrawFramedImage(canvas, image, rect);
            textLeft = rect.Right + 110;
        }

        // Título (Anton, en mayúsculas) + línea de acento + subtítulo.
        var maxWidth = textRight - textLeft;
        var titleSize = 132f;
        List<string> titleLines;
        using var titleFont = new SKFont(Display.Value, titleSize);
        while (true)
        {
            titleFont.Size = titleSize;
            titleLines = Wrap(title.ToUpperInvariant(), titleFont, maxWidth);
            if (titleLines.Count <= 3 || titleSize <= 70) break;
            titleSize -= 8;
        }
        using var detailFont = new SKFont(Body.Value, 54);
        var detailLines = string.IsNullOrWhiteSpace(detail) ? [] : Wrap(detail, detailFont, maxWidth);
        var lineH = titleSize * 1.08f;
        var blockH = titleLines.Count * lineH + (detailLines.Count > 0 ? 50 + detailLines.Count * 66 : 0);
        var y = (H - blockH) / 2 - 20 + titleSize * 0.9f;
        // Sin imagen, el texto va centrado: una placa con solo un nombre ("NIGHT CITY")
        // corrida a la izquierda parecía a medio hacer.
        var centered = image is null;
        float X(string line, SKFont f) => centered ? (W - f.MeasureText(line)) / 2 : textLeft;
        foreach (var line in titleLines)
        {
            DrawShadowed(canvas, line, X(line, titleFont), y, titleFont, SKColors.White);
            y += lineH;
        }
        if (detailLines.Count > 0)
        {
            using var bar = new SKPaint { Color = Accent, IsAntialias = true };
            canvas.DrawRoundRect(SKRect.Create(centered ? (W - 160) / 2f : textLeft, y - titleSize * 0.55f, 160, 10), 5, 5, bar);
            y += 40;
            foreach (var line in detailLines)
            {
                DrawShadowed(canvas, line, X(line, detailFont), y, detailFont, new SKColor(0xE6, 0xF7, 0xFF));
                y += 66;
            }
        }
        Save(surface, output);
    }

    /// <summary>
    /// Panel de logos: uno o varios, cada uno sobre una placa clara (casi todos
    /// los logos son oscuros sobre transparente), separados por "×". Un logo que
    /// no se encontró se muestra como su nombre escrito.
    /// </summary>
    public static void RenderLogos(IReadOnlyList<(string Name, string? Path)> logos, string? backgroundPath, string output)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var canvas = surface.Canvas;
        DrawBackground(canvas, backgroundPath);

        var n = Math.Max(1, logos.Count);
        const float gap = 120;
        var plateW = Math.Min(760f, (W - 320 - gap * (n - 1)) / n);
        var plateH = Math.Min(460f, plateW * 0.62f);
        var x = (W - (plateW * n + gap * (n - 1))) / 2;
        var top = (H - plateH) / 2 - 30;
        using var plate = new SKPaint { Color = new SKColor(0xF4, 0xF6, 0xFA), IsAntialias = true };
        using var darkPlate = new SKPaint { Color = Ink, IsAntialias = true };
        using var glow = new SKPaint { Color = Accent.WithAlpha(150), IsAntialias = true, ImageFilter = SKImageFilter.CreateBlur(28, 28) };
        using var nameFont = new SKFont(Display.Value, 84);
        using var timesFont = new SKFont(Body.Value, 110);
        for (var i = 0; i < logos.Count; i++)
        {
            var rect = SKRect.Create(x, top, plateW, plateH);
            using var bitmap = logos[i].Path is null ? null : SKBitmap.Decode(logos[i].Path);
            canvas.DrawRoundRect(rect, 34, 34, glow);
            // Un logo claro (el del canal) se pierde sobre la placa blanca: va sobre una oscura.
            canvas.DrawRoundRect(rect, 34, 34, bitmap is not null && IsLightLogo(bitmap) ? darkPlate : plate);
            if (bitmap is not null)
            {
                var inner = SKRect.Inflate(rect, -plateW * 0.1f, -plateH * 0.14f);
                // Sin el margen transparente: muchos logos vienen en un lienzo enorme y quedaban chicos.
                var src = OpaqueBounds(bitmap);
                using var logoPaint = new SKPaint { IsAntialias = true };
                canvas.DrawBitmap(bitmap, src, Fit(src.Width, src.Height, inner), logoPaint);
            }
            else
            {
                var lines = Wrap(logos[i].Name.ToUpperInvariant(), nameFont, plateW * 0.85f);
                var ly = rect.MidY - (lines.Count - 1) * 45 + 30;
                using var ink = new SKPaint { Color = Ink, IsAntialias = true };
                foreach (var line in lines)
                {
                    canvas.DrawText(line, rect.MidX, ly, SKTextAlign.Center, nameFont, ink);
                    ly += 90;
                }
            }
            if (i + 1 < logos.Count)
            {
                using var times = new SKPaint { Color = Accent2, IsAntialias = true };
                canvas.DrawText("×", rect.Right + gap / 2, rect.MidY + 38, SKTextAlign.Center, timesFont, times);
            }
            x += plateW + gap;
        }
        Save(surface, output);
    }

    /// <summary>
    /// Retratos (personajes, personas): uno o varios lado a lado, enmarcados, con
    /// el nombre debajo — así quien mira sabe quién es cuando se lo nombra.
    /// </summary>
    public static void RenderPortraits(IReadOnlyList<(string Name, string Path)> people, string? backgroundPath, string output)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var canvas = surface.Canvas;
        DrawBackground(canvas, backgroundPath ?? people.FirstOrDefault().Path);
        var bitmaps = people.Select(p => (p.Name, Bitmap: SKBitmap.Decode(p.Path))).Where(p => p.Bitmap is not null).ToList();
        try
        {
            if (bitmaps.Count == 0) { Save(surface, output); return; }
            const float gap = 90, maxH = 640;
            var widths = bitmaps.Select(b => Math.Min(maxH * b.Bitmap.Width / b.Bitmap.Height, 820f)).ToList();
            var scale = Math.Min(1f, (W - 300 - gap * (bitmaps.Count - 1)) / widths.Sum());
            var x = (W - (widths.Sum() * scale + gap * (bitmaps.Count - 1))) / 2;
            using var nameFont = new SKFont(Display.Value, bitmaps.Count > 2 ? 56 : 68);
            for (var i = 0; i < bitmaps.Count; i++)
            {
                var w = widths[i] * scale;
                var h = w * bitmaps[i].Bitmap.Height / bitmaps[i].Bitmap.Width;
                var rect = SKRect.Create(x, 130 + (maxH * scale - h) / 2, w, h);
                DrawFramedImage(canvas, bitmaps[i].Bitmap, rect);
                var label = bitmaps[i].Name.ToUpperInvariant();
                using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 190), IsAntialias = true, ImageFilter = SKImageFilter.CreateBlur(6, 6) };
                using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
                var ly = 130 + maxH * scale + 90;
                canvas.DrawText(label, rect.MidX + 3, ly + 4, SKTextAlign.Center, nameFont, shadow);
                canvas.DrawText(label, rect.MidX, ly, SKTextAlign.Center, nameFont, paint);
                x += w + gap;
            }
            Save(surface, output);
        }
        finally
        {
            foreach (var b in bitmaps) b.Bitmap.Dispose();
        }
    }

    /// <summary>¿El logo es claro? Brillo medio de los píxeles visibles (no transparentes) sobre 200.</summary>
    public static bool IsLightLogo(SKBitmap bitmap)
    {
        double sum = 0;
        var n = 0;
        var step = Math.Max(1, Math.Min(bitmap.Width, bitmap.Height) / 60);
        for (var y = 0; y < bitmap.Height; y += step)
            for (var x = 0; x < bitmap.Width; x += step)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Alpha < 128) continue;
                sum += 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
                n++;
            }
        return n > 0 && sum / n > 170;
    }

    /// <summary>Rectángulo con los píxeles visibles (alfa &gt; 16) del logo.</summary>
    public static SKRect OpaqueBounds(SKBitmap bitmap)
    {
        int minX = bitmap.Width, minY = bitmap.Height, maxX = -1, maxY = -1;
        var step = Math.Max(1, Math.Min(bitmap.Width, bitmap.Height) / 400);
        for (var y = 0; y < bitmap.Height; y += step)
            for (var x = 0; x < bitmap.Width; x += step)
                if (bitmap.GetPixel(x, y).Alpha > 16)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        return maxX < 0 ? SKRect.Create(bitmap.Width, bitmap.Height)
            : new SKRect(minX, minY, Math.Min(bitmap.Width, maxX + step), Math.Min(bitmap.Height, maxY + step));
    }

    private static void DrawBackground(SKCanvas canvas, string? path)
    {
        canvas.Clear(Ink);
        using var bitmap = path is null ? null : SKBitmap.Decode(path);
        if (bitmap is not null)
        {
            using var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(36, 36), IsAntialias = true };
            canvas.DrawBitmap(bitmap, Fill(bitmap.Width, bitmap.Height, SKRect.Create(-80, -80, W + 160, H + 160)), blur);
        }
        using var shade = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(W, H),
                [Ink.WithAlpha(bitmap is null ? (byte)255 : (byte)215), new SKColor(0x1A, 0x0B, 0x2E, bitmap is null ? (byte)255 : (byte)190)],
                SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(SKRect.Create(W, H), shade);
    }

    private static void DrawFramedImage(SKCanvas canvas, SKBitmap image, SKRect rect)
    {
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 180), ImageFilter = SKImageFilter.CreateBlur(30, 30) };
        canvas.DrawRoundRect(SKRect.Inflate(rect, 6, 6), 22, 22, shadow);
        using var border = new SKPaint { Color = Accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6 };
        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(rect, 18, 18), antialias: true);
        canvas.DrawBitmap(image, Fill(image.Width, image.Height, rect), new SKPaint { IsAntialias = true });
        canvas.Restore();
        canvas.DrawRoundRect(rect, 18, 18, border);
    }

    private static void DrawShadowed(SKCanvas canvas, string text, float x, float y, SKFont font, SKColor color)
    {
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 190), IsAntialias = true, ImageFilter = SKImageFilter.CreateBlur(6, 6) };
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawText(text, x + 3, y + 4, font, shadow);
        canvas.DrawText(text, x, y, font, paint);
    }

    /// <summary>Corta un texto en líneas que entran en <paramref name="maxWidth"/>.</summary>
    public static List<string> Wrap(string text, SKFont font, float maxWidth)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && font.MeasureText(candidate) > maxWidth)
            {
                lines.Add(current);
                current = word;
            }
            else current = candidate;
        }
        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    private static SKRect Fit(float w, float h, SKRect box)
    {
        var scale = Math.Min(box.Width / w, box.Height / h);
        return SKRect.Create(box.MidX - w * scale / 2, box.MidY - h * scale / 2, w * scale, h * scale);
    }

    private static SKRect Fill(float w, float h, SKRect box)
    {
        var scale = Math.Max(box.Width / w, box.Height / h);
        return SKRect.Create(box.MidX - w * scale / 2, box.MidY - h * scale / 2, w * scale, h * scale);
    }

    private static void Save(SKSurface surface, string output)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        using var file = File.Create(output);
        data.SaveTo(file);
    }
}
