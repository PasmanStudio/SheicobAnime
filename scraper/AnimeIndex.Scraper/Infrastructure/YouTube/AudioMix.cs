using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>Audio PCM 16 bits en memoria (WAV). Muestras intercaladas por canal.</summary>
public sealed record PcmAudio(int SampleRate, int Channels, short[] Samples)
{
    public int Frames => Samples.Length / Channels;
    public double Seconds => (double)Frames / SampleRate;

    /// <summary>Lee un WAV PCM s16le (busca los chunks "fmt " y "data"; ignora el resto).</summary>
    public static PcmAudio Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            throw new InvalidOperationException($"{Path.GetFileName(path)} no es un WAV");
        int rate = 0, channels = 0, bits = 0, format = 0;
        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, pos, 4);
            var size = BitConverter.ToInt32(bytes, pos + 4);
            var body = pos + 8;
            if (id == "fmt ")
            {
                format = BitConverter.ToInt16(bytes, body);
                channels = BitConverter.ToInt16(bytes, body + 2);
                rate = BitConverter.ToInt32(bytes, body + 4);
                bits = BitConverter.ToInt16(bytes, body + 14);
            }
            else if (id == "data")
            {
                // ffmpeg escribe 0xFFFFFFFF si no pudo volver a la cabecera: hasta el final.
                var length = size < 0 || body + size > bytes.Length ? bytes.Length - body : size;
                if (format != 1 || bits != 16)
                    throw new InvalidOperationException($"{Path.GetFileName(path)}: se esperaba PCM 16 bits (formato {format}, {bits} bits)");
                var samples = new short[length / 2];
                Buffer.BlockCopy(bytes, body, samples, 0, samples.Length * 2);
                return new PcmAudio(rate, channels, samples);
            }
            pos = body + size + (size & 1);
        }
        throw new InvalidOperationException($"{Path.GetFileName(path)}: WAV sin datos");
    }

    public void Write(string path)
    {
        var dataBytes = Samples.Length * 2;
        using var w = new BinaryWriter(File.Create(path));
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataBytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)Channels);
        w.Write(SampleRate); w.Write(SampleRate * Channels * 2); w.Write((short)(Channels * 2)); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataBytes);
        var buffer = new byte[dataBytes];
        Buffer.BlockCopy(Samples, 0, buffer, 0, dataBytes);
        w.Write(buffer);
    }
}

/// <summary>
/// Armado del audio del video en código, muestra por muestra: unir la voz de las
/// escenas y mezclar la música con ducking. Antes lo hacían filtros de ffmpeg
/// (concat, sidechaincompress + amix con la música en -stream_loop) y con el
/// ffmpeg 6.1 del runner el AAC final salió con 71 huecos de timestamps
/// (32,5 s de voz perdidos en un video de 10 min, sobre todo al arrancar cada
/// escena; render 46030431471). Con ffmpeg 9 local no pasaba. Acá no se puede
/// perder una muestra: el largo de cada paso se verifica.
/// </summary>
public static class AudioMix
{
    /// <summary>Volumen final (YouTube normaliza a ~−14 LUFS; el video salía en −22).</summary>
    public const double TargetLufs = -14;

    /// <summary>
    /// Une las escenas en orden, cada una seguida de <paramref name="gaps"/>[i] s de
    /// silencio. Todas tienen que tener el mismo formato. Público para tests.
    /// </summary>
    public static PcmAudio Join(IReadOnlyList<PcmAudio> parts, IReadOnlyList<double> gaps)
    {
        if (parts.Count == 0) throw new ArgumentException("sin escenas", nameof(parts));
        var rate = parts[0].SampleRate;
        var ch = parts[0].Channels;
        if (parts.Any(p => p.SampleRate != rate || p.Channels != ch))
            throw new InvalidOperationException("las voces de las escenas tienen formatos distintos");
        var gapFrames = gaps.Select(g => (int)Math.Round(g * rate)).ToList();
        var total = parts.Sum(p => (long)p.Samples.Length) + gapFrames.Sum(g => (long)g * ch);
        var output = new short[total];
        long at = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            Array.Copy(parts[i].Samples, 0, output, at, parts[i].Samples.Length);
            at += parts[i].Samples.Length + (long)(i < gapFrames.Count ? gapFrames[i] : 0) * ch;
        }
        return new PcmAudio(rate, ch, output);
    }

    /// <summary>
    /// Voz + música (mismo formato) con la música a <paramref name="volume"/>,
    /// fundido de entrada de 2 s y de salida de 3 s, y ducking contra la voz
    /// (umbral 0,02, ratio 3, ataque 150 ms, release 2 s: los mismos valores que
    /// usaba sidechaincompress). El largo es el de la voz. Público para tests.
    /// </summary>
    public static PcmAudio Mix(PcmAudio voice, PcmAudio? music, double volume,
        double threshold = 0.02, double ratio = 3, double attackMs = 150, double releaseMs = 2000)
    {
        if (music is null || volume <= 0) return voice;
        if (music.SampleRate != voice.SampleRate || music.Channels != voice.Channels)
            throw new InvalidOperationException("voz y música con formatos distintos");
        int rate = voice.SampleRate, ch = voice.Channels, frames = voice.Frames;
        var output = new short[voice.Samples.Length];
        var attack = 1 - Math.Exp(-1.0 / (attackMs / 1000 * rate));
        var release = 1 - Math.Exp(-1.0 / (releaseMs / 1000 * rate));
        var fadeIn = 2.0 * rate;
        var fadeOutFrom = frames - 3.0 * rate;
        double env = 0; // potencia media de la voz (detección RMS)
        for (var f = 0; f < frames; f++)
        {
            double power = 0;
            for (var c = 0; c < ch; c++)
            {
                var x = voice.Samples[f * ch + c] / 32768.0;
                power += x * x;
            }
            power /= ch;
            env += (power - env) * (power > env ? attack : release);
            var level = Math.Sqrt(env);
            var duck = level > threshold ? Math.Pow(level / threshold, 1 / ratio - 1) : 1;
            var fade = Math.Min(1, f / fadeIn) * (f > fadeOutFrom ? Math.Max(0, (frames - f) / (3.0 * rate)) : 1);
            var gain = volume * duck * fade;
            for (var c = 0; c < ch; c++)
            {
                var i = f * ch + c;
                var m = i < music.Samples.Length ? music.Samples[i] : 0;
                output[i] = (short)Math.Clamp(voice.Samples[i] + m * gain, short.MinValue, short.MaxValue);
            }
        }
        return new PcmAudio(rate, ch, output);
    }

    /// <summary>Recorta o completa con silencio hasta <paramref name="frames"/> cuadros. Público para tests.</summary>
    public static PcmAudio Fit(PcmAudio audio, int frames)
    {
        var samples = new short[frames * audio.Channels];
        Array.Copy(audio.Samples, samples, Math.Min(samples.Length, audio.Samples.Length));
        return audio with { Samples = samples };
    }

    /// <summary>Primera pasada de loudnorm: mide el audio. Público para tests.</summary>
    public static List<string> LoudnessMeasureArgs(string input) =>
        ["-hide_banner", "-nostats", "-i", input, "-af",
         $"loudnorm=I={TargetLufs.ToString(CultureInfo.InvariantCulture)}:TP=-1.5:LRA=20:print_format=json", "-f", "null", "-"];

    /// <summary>
    /// Segunda pasada: aplica la ganancia medida en modo lineal (solo sube o baja).
    /// Si subir hasta −14 haría pasar el pico de −1,5 dBTP, loudnorm pasa solo a
    /// modo dinámico y limita los picos. Público para tests.
    /// </summary>
    public static List<string> LoudnessApplyArgs(string input, string output, IReadOnlyDictionary<string, string> measured)
    {
        string M(string key) => measured.TryGetValue(key, out var v) ? v : throw new InvalidOperationException($"loudnorm no midió {key}");
        var filter = $"loudnorm=I={TargetLufs.ToString(CultureInfo.InvariantCulture)}:TP=-1.5:LRA=20" +
                     $":measured_I={M("input_i")}:measured_TP={M("input_tp")}:measured_LRA={M("input_lra")}" +
                     $":measured_thresh={M("input_thresh")}:offset={M("target_offset")}:linear=true";
        return ["-y", "-i", input, "-af", filter, "-ar", "48000", "-c:a", "pcm_s16le", output];
    }

    /// <summary>El JSON que loudnorm imprime al final de stderr → campo → valor. Público para tests.</summary>
    public static Dictionary<string, string> ParseLoudness(string stderr)
    {
        var start = stderr.LastIndexOf('{');
        var end = stderr.LastIndexOf('}');
        if (start < 0 || end < start) throw new InvalidOperationException("loudnorm no devolvió medidas");
        using var doc = JsonDocument.Parse(stderr[start..(end + 1)]);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString());
    }

    /// <summary>
    /// Huecos en los timestamps de los paquetes de audio (lista de pts_time de
    /// ffprobe): (desde, hasta) de cada salto mayor a <paramref name="tolerance"/> s
    /// por sobre la duración típica de un paquete. Público para tests.
    /// </summary>
    public static List<(double From, double To)> PtsGaps(IReadOnlyList<double> pts, double tolerance = 0.03)
    {
        var gaps = new List<(double, double)>();
        if (pts.Count < 3) return gaps;
        var steps = pts.Zip(pts.Skip(1), (a, b) => b - a).Where(d => d > 0).OrderBy(d => d).ToList();
        var typical = steps.Count > 0 ? steps[steps.Count / 2] : 0;
        for (var i = 1; i < pts.Count; i++)
            if (pts[i] - pts[i - 1] > typical + tolerance)
                gaps.Add((pts[i - 1], pts[i]));
        return gaps;
    }

    /// <summary>pts_time por línea (salida csv de ffprobe) → números.</summary>
    public static List<double> ParsePts(string csv) =>
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => double.TryParse(l.Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : double.NaN)
            .Where(t => !double.IsNaN(t)).ToList();
}
