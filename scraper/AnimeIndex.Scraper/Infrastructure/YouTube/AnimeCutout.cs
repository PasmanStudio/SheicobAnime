using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>
/// Recorta al personaje de una imagen de anime (fondo transparente) para la
/// miniatura. Modelo ISNet entrenado en arte anime de SkyTNT (skytnt/anime-seg,
/// isnetis.onnx, Apache-2.0, ~176 MB): el mismo que rembg llama "isnet-anime".
/// No usar el modelo por defecto de rembg (BRIA): su licencia no permite uso comercial.
/// CPU, una pasada de 1024x1024 por imagen (unos segundos).
/// </summary>
public sealed class AnimeCutout : IDisposable
{
    // Revisión fija y hash verificado: si el archivo cambia en Hugging Face, no entra sin aviso.
    public const string ModelUrl = "https://huggingface.co/skytnt/anime-seg/resolve/493cb60893f47441b26ec4fb9a306bce9e342982/isnetis.onnx";
    public const string ModelSha256 = "f15622d853e8260172812b657053460e20806f04b9e05147d49af7bed31a6e99";
    private const int Size = 1024;
    private readonly InferenceSession _session;

    private AnimeCutout(string modelPath) =>
        _session = new InferenceSession(modelPath, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });

    /// <summary>Dónde vive el modelo (la caché de Hugging Face del runner, que ya se guarda entre corridas).</summary>
    public static string DefaultModelPath() => Path.Combine(
        Environment.GetEnvironmentVariable("HF_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "huggingface"),
        "sheicob", "isnetis.onnx");

    /// <summary>Carga el modelo, bajándolo la primera vez.</summary>
    public static async Task<AnimeCutout> LoadAsync(HttpClient http, string? modelPath, CancellationToken ct)
    {
        modelPath ??= DefaultModelPath();
        if (!File.Exists(modelPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            var tmp = modelPath + ".part";
            await using (var src = await http.GetStreamAsync(ModelUrl, ct))
            await using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst, ct);
            if (!HashMatches(tmp))
            {
                File.Delete(tmp);
                throw new IOException("isnetis.onnx no coincide con el SHA-256 esperado");
            }
            File.Move(tmp, modelPath, overwrite: true);
        }
        else if (!HashMatches(modelPath))
        {
            File.Delete(modelPath); // caché vieja o corrupta: se vuelve a bajar en la próxima corrida
            throw new IOException("isnetis.onnx en caché no coincide con el SHA-256 esperado");
        }
        return new AnimeCutout(modelPath);
    }

    /// <summary>
    /// Imagen → la misma imagen con alfa = máscara del personaje. Devuelve también
    /// qué fracción del cuadro ocupa (si es muy poco, el recorte no sirve).
    /// </summary>
    public (SKBitmap Cutout, double Coverage) Cut(SKBitmap source)
    {
        // Como el código de referencia: escalar el lado mayor a 1024 y rellenar a cuadrado.
        var scale = (float)Size / Math.Max(source.Width, source.Height);
        int w = Math.Max(1, (int)Math.Round(source.Width * scale)), h = Math.Max(1, (int)Math.Round(source.Height * scale));
        int ox = (Size - w) / 2, oy = (Size - h) / 2;
        using var square = new SKBitmap(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(square))
        {
            canvas.Clear(SKColors.Black);
            using var resized = source.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
            canvas.DrawBitmap(resized, ox, oy);
        }

        var input = new DenseTensor<float>([1, 3, Size, Size]);
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var c = square.GetPixel(x, y);
                input[0, 0, y, x] = c.Red / 255f;
                input[0, 1, y, x] = c.Green / 255f;
                input[0, 2, y, x] = c.Blue / 255f;
            }
        var name = _session.InputMetadata.Keys.First();
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(name, input)]);
        var mask = results.First().AsTensor<float>();

        // Máscara 1024 → recorte del área real → tamaño original, como alfa.
        using var maskBitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                { var v = (byte)Math.Clamp(mask[0, 0, y + oy, x + ox] * 255, 0, 255); maskBitmap.SetPixel(x, y, new SKColor(v, v, v)); }
        using var fullMask = maskBitmap.Resize(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Opaque),
            new SKSamplingOptions(SKFilterMode.Linear));

        var output = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        long covered = 0;
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
            {
                var a = (byte)(fullMask.GetPixel(x, y).Red * EdgeFade(x, y, source.Width, source.Height));
                if (a > 127) covered++;
                var c = source.GetPixel(x, y);
                output.SetPixel(x, y, new SKColor(c.Red, c.Green, c.Blue, a));
            }
        return (output, (double)covered / (source.Width * (long)source.Height));
    }

    /// <summary>
    /// Fundido hacia los bordes de la imagen original (8 % a los costados y arriba,
    /// 22 % abajo): los retratos de AniList y de la wiki son recortes de capturas, y
    /// lo que el modelo deja pegado al personaje (un asiento, un hombro cortado) se
    /// veía como un corte recto. Público para tests.
    /// </summary>
    public static float EdgeFade(int x, int y, int width, int height)
    {
        static float Ramp(float d, float span) => span <= 0 ? 1 : Math.Clamp(d / span, 0, 1);
        var side = Math.Min(Ramp(x, width * 0.08f), Ramp(width - 1 - x, width * 0.08f));
        var top = Ramp(y, height * 0.08f);
        var bottom = Ramp(height - 1 - y, height * 0.22f);
        var f = side * top * bottom;
        return f * f * (3 - 2 * f); // suave (smoothstep)
    }

    private static bool HashMatches(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))
            .Equals(ModelSha256, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _session.Dispose();
}
