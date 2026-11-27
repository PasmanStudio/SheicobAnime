using SkiaSharp;

namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>Un personaje para la miniatura: recortado (fondo transparente) o, si el recorte falló, la imagen tal cual.</summary>
public sealed record ThumbnailSubject(SKBitmap Image, bool IsCutout);

/// <summary>Lo que se dibuja: fondo, 0 a 2 personajes y el gancho (1 a 4 palabras; null = sin texto).</summary>
public sealed record ThumbnailSpec(SKBitmap? Background, IReadOnlyList<ThumbnailSubject> Subjects, string? Text);

/// <summary>Resultado de los chequeos automáticos de una miniatura.</summary>
public sealed record ThumbnailReport(SKRect TextBounds, float FontSize, float FeedCapHeight, IReadOnlyList<string> Warnings)
{
    public bool Ok => Warnings.Count == 0;
}

/// <summary>
/// Miniaturas de YouTube 1280x720 al estilo de los canales de anime (ver
/// reports/Miniaturas de videos de anime.md): fondo de arte oficial a sangre,
/// oscurecido y desenfocado (nunca con franjas: antes la miniatura era el banner
/// ultraancho de AniList con bandas borrosas); el o los personajes recortados,
/// grandes, con brillo de color y sombra; y un gancho de 1 a 4 palabras en Anton,
/// blanco con contorno negro grueso y una palabra en amarillo, del lado libre.
/// El texto nunca va abajo a la derecha (ahí YouTube pone la duración) y tiene que
/// leerse en el feed del celular (~168 px de ancho).
/// </summary>
public static class ThumbnailRenderer
{
    public const int W = 1280, H = 720;

    /// <summary>Zona de la duración del video: abajo a la derecha (22 % x 18 %).</summary>
    public static readonly SKRect DurationBadge = new(W * 0.78f, H * 0.82f, W, H);

    /// <summary>Ancho de la miniatura en el feed del celular.</summary>
    public const float FeedWidth = 168;

    /// <summary>Altura mínima de las mayúsculas a tamaño de feed para que se lean.</summary>
    public const float MinFeedCapHeight = 9;

    public static readonly SKColor Highlight = new(0xFF, 0xD6, 0x0A);
    public static readonly SKColor Glow = new(0x14, 0xB1, 0xE7);

    private static readonly Lazy<SKTypeface> Display = new(() =>
    {
        using var s = typeof(ThumbnailRenderer).Assembly.GetManifestResourceStream("AnimeIndex.Scraper.Resources.Anton-Regular.ttf");
        return (s is null ? null : SKTypeface.FromStream(s)) ?? SKTypeface.Default;
    });

    public static ThumbnailReport Render(ThumbnailSpec spec, string output)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var canvas = surface.Canvas;
        DrawBackground(canvas, spec.Background);
        DrawSubjects(canvas, spec.Subjects, hasText: !string.IsNullOrWhiteSpace(spec.Text));
        var report = string.IsNullOrWhiteSpace(spec.Text)
            ? new ThumbnailReport(SKRect.Empty, 0, 0, [])
            : DrawText(canvas, spec.Text!, spec.Subjects.Count > 0);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        using var file = File.Create(output);
        data.SaveTo(file);
        return report;
    }

    // ── Fondo ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rectángulo de origen para llenar <paramref name="w"/>x<paramref name="h"/> sin
    /// franjas ("cover"): recorta lo que sobra, centrado. Público para tests.
    /// </summary>
    public static SKRect CoverSource(int srcW, int srcH, int w, int h)
    {
        var scale = Math.Max((float)w / srcW, (float)h / srcH);
        float cw = w / scale, ch = h / scale;
        return SKRect.Create((srcW - cw) / 2, (srcH - ch) / 2, cw, ch);
    }

    private static void DrawBackground(SKCanvas canvas, SKBitmap? background)
    {
        canvas.Clear(new SKColor(0x0B, 0x0B, 0x14));
        if (background is not null)
        {
            using var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(7, 7) };
            using var image = SKImage.FromBitmap(background);
            canvas.DrawImage(image, CoverSource(background.Width, background.Height, W, H),
                SKRect.Create(-20, -20, W + 40, H + 40), new SKSamplingOptions(SKCubicResampler.Mitchell), blur);
        }
        // Oscurecer más del lado del texto (izquierda) y viñeta para que el personaje resalte.
        using var shade = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(W, 0),
                [new SKColor(0, 0, 0, 215), new SKColor(0, 0, 0, 120), new SKColor(0, 0, 0, 90)], [0, 0.55f, 1], SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(0, 0, W, H, shade);
        using var vignette = new SKPaint
        {
            Shader = SKShader.CreateRadialGradient(new SKPoint(W * 0.62f, H * 0.45f), W * 0.75f,
                [SKColors.Transparent, new SKColor(0, 0, 0, 170)], [0.55f, 1], SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(0, 0, W, H, vignette);
    }

    // ── Personajes ───────────────────────────────────────────────────────────

    /// <summary>
    /// Dónde va cada personaje (rectángulo de destino), del lado derecho si hay
    /// texto. Uno: grande, cortado abajo. Dos: lado a lado, el primero más
    /// adelante. Público para tests.
    /// </summary>
    public static List<SKRect> SubjectRects(IReadOnlyList<(int Width, int Height)> sizes, bool hasText)
    {
        var rects = new List<SKRect>();
        if (sizes.Count == 0) return rects;
        var centers = sizes.Count == 1
            ? [hasText ? W * 0.72f : W * 0.5f]
            : hasText ? new[] { W * 0.60f, W * 0.84f } : [W * 0.33f, W * 0.67f];
        var height = sizes.Count == 1 ? H * 1.12f : H * 1.02f;
        for (var i = 0; i < Math.Min(2, sizes.Count); i++)
        {
            var (w, h) = sizes[i];
            var dh = height;
            var dw = dh * w / h;
            var maxW = sizes.Count == 1 ? W * 0.62f : W * 0.42f;
            if (dw > maxW) { dw = maxW; dh = dw * h / w; }
            // Pegado abajo (se corta en el borde, como un plano medio).
            rects.Add(SKRect.Create(centers[i] - dw / 2, H - dh * 0.92f, dw, dh));
        }
        return rects;
    }

    private static void DrawSubjects(SKCanvas canvas, IReadOnlyList<ThumbnailSubject> subjects, bool hasText)
    {
        var rects = SubjectRects(subjects.Select(s => (s.Image.Width, s.Image.Height)).ToList(), hasText);
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        // El de atrás primero: el segundo queda detrás del primero.
        for (var i = rects.Count - 1; i >= 0; i--)
        {
            var s = subjects[i];
            var rect = rects[i];
            using var image = SKImage.FromBitmap(s.Image);
            if (s.IsCutout)
            {
                // Brillo de color alrededor de la silueta + sombra oscura detrás.
                using var glow = new SKPaint
                {
                    ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 0, 16, 16, Glow.WithAlpha(230)),
                };
                canvas.DrawImage(image, rect, sampling, glow);
                using var shadow = new SKPaint
                {
                    ImageFilter = SKImageFilter.CreateDropShadowOnly(-14, 10, 18, 18, new SKColor(0, 0, 0, 200)),
                };
                canvas.DrawImage(image, rect, sampling, shadow);
                canvas.DrawImage(image, rect, sampling);
            }
            else
            {
                // Sin recorte: retrato enmarcado, un poco inclinado.
                var framed = SKRect.Create(rect.MidX - rect.Width * 0.42f, H * 0.1f, rect.Width * 0.84f, H * 0.8f);
                var src = CoverSource(s.Image.Width, s.Image.Height, (int)framed.Width, (int)framed.Height);
                canvas.Save();
                canvas.RotateDegrees(i == 0 ? -3 : 3, framed.MidX, framed.MidY);
                using var border = new SKPaint { Color = SKColors.White, IsAntialias = true };
                using var shadow = new SKPaint { ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 12, 20, 20, new SKColor(0, 0, 0, 220)) };
                var outer = SKRect.Inflate(framed, 10, 10);
                canvas.DrawRoundRect(outer, 18, 18, shadow);
                canvas.DrawRoundRect(outer, 18, 18, border);
                using var clip = new SKPath();
                clip.AddRoundRect(framed, 12, 12);
                canvas.ClipPath(clip, antialias: true);
                canvas.DrawImage(image, src, framed, sampling);
                canvas.Restore();
            }
        }
    }

    // ── Texto ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parte el gancho en 1 o 2 líneas parejas (por ancho). Una palabra entre
    /// asteriscos se resalta en amarillo; si no hay, la última. Público para tests.
    /// </summary>
    public static (List<List<(string Word, bool Highlight)>> Lines, int Count) Layout(string text)
    {
        var raw = text.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        // "*TRAICIÓN*?": la marca puede venir pegada a signos.
        var marked = raw.FindIndex(w => System.Text.RegularExpressions.Regex.IsMatch(w, @"\*[^*]+\*"));
        var words = raw.Select((w, i) => (Word: w.Replace("*", ""), Highlight: marked >= 0 ? i == marked : i == raw.Count - 1)).ToList();
        if (words.Count <= 2 && string.Concat(words.Select(w => w.Word)).Length <= 12)
            return ([words], words.Count);
        // Dos líneas: el corte que deja las dos más parejas en letras.
        var best = 1;
        var bestDiff = int.MaxValue;
        for (var cut = 1; cut < words.Count; cut++)
        {
            var a = words.Take(cut).Sum(w => w.Word.Length + 1);
            var b = words.Skip(cut).Sum(w => w.Word.Length + 1);
            if (Math.Abs(a - b) < bestDiff) { bestDiff = Math.Abs(a - b); best = cut; }
        }
        return ([words.Take(best).ToList(), words.Skip(best).ToList()], words.Count);
    }

    private static ThumbnailReport DrawText(SKCanvas canvas, string text, bool hasSubjects)
    {
        var warnings = new List<string>();
        var (lines, count) = Layout(text);
        if (count > 4) warnings.Add($"el gancho tiene {count} palabras (máximo 4)");

        // Caja del texto: el lado izquierdo si hay personajes; el centro si no.
        var box = hasSubjects ? SKRect.Create(52, 56, W * 0.47f, H * 0.70f) : SKRect.Create(90, 90, W - 180, H * 0.62f);
        using var font = new SKFont(Display.Value, 200) { Edging = SKFontEdging.Antialias };
        float LineWidth(List<(string Word, bool Highlight)> line) => font.MeasureText(string.Join(' ', line.Select(w => w.Word)));
        // El más grande que entra en ancho y alto (búsqueda binaria).
        float lo = 40, hi = 230;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            font.Size = mid;
            var fits = lines.All(l => LineWidth(l) <= box.Width) && lines.Count * mid * 1.12f <= box.Height;
            if (fits) lo = mid; else hi = mid;
        }
        font.Size = lo;
        var lineHeight = font.Size * 1.1f; // aire para tildes (Ó) y la ¿ de la línea de abajo
        var capHeight = font.Metrics.CapHeight > 0 ? font.Metrics.CapHeight : font.Size * 0.73f;
        var total = lineHeight * (lines.Count - 1) + capHeight;
        var y = box.Top + (box.Height - total) / 2 + capHeight;

        using var stroke = new SKPaint
        {
            Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = font.Size * 0.14f, StrokeJoin = SKStrokeJoin.Round,
        };
        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 200), IsAntialias = true,
            ImageFilter = SKImageFilter.CreateBlur(font.Size * 0.06f, font.Size * 0.06f),
        };
        using var fill = new SKPaint { IsAntialias = true };
        var bounds = SKRect.Empty;
        foreach (var line in lines)
        {
            var x = box.Left;
            var space = font.MeasureText(" ");
            foreach (var (word, highlight) in line)
            {
                var wordWidth = font.MeasureText(word);
                canvas.DrawText(word, x + font.Size * 0.04f, y + font.Size * 0.06f, font, shadow);
                canvas.DrawText(word, x, y, font, stroke);
                fill.Color = highlight ? Highlight : SKColors.White;
                canvas.DrawText(word, x, y, font, fill);
                var r = SKRect.Create(x, y - capHeight, wordWidth, capHeight);
                bounds = bounds.IsEmpty ? r : SKRect.Union(bounds, r);
                x += wordWidth + space;
            }
            y += lineHeight;
        }

        var feedCap = capHeight * FeedWidth / W;
        if (feedCap < MinFeedCapHeight) warnings.Add($"el texto no se lee en el feed ({feedCap:F1} px de mayúscula, mínimo {MinFeedCapHeight})");
        if (bounds.IntersectsWith(DurationBadge)) warnings.Add("el texto pisa la duración del video");
        return new ThumbnailReport(bounds, font.Size, feedCap, warnings);
    }
}
