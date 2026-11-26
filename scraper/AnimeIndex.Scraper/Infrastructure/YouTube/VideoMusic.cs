using System.Globalization;
using System.Text;
using AnimeIndex.Scraper.Infrastructure.Instagram;

namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>
/// Música de fondo de los videos de YouTube. Usa la biblioteca CC BY de los
/// reels (<see cref="ReelMusicService.Library"/>, Kevin MacLeod): libre para uso
/// comercial con crédito, que va en la descripción. Nunca OSTs ni música
/// comercial (Content ID).
///
/// Mezcla: varios tracks del mismo mood unidos con fundido cruzado, en loop
/// hasta cubrir el video, bajos y con "ducking": bajan solos cuando habla la voz
/// (sidechaincompress) y suben un poco en las pausas. La voz no se toca.
/// </summary>
public static class VideoMusic
{
    /// <summary>Volumen de la música respecto del original (0,12 ≈ −18 dB). 0 = sin música.</summary>
    public const double DefaultVolume = 0.12;

    /// <summary>Fundido cruzado entre tracks.</summary>
    public const double CrossfadeSeconds = 3;

    /// <summary>
    /// <paramref name="count"/> tracks distintos del mood, empezando en un punto
    /// fijo por video (hash de <paramref name="seed"/>): el mismo plan suena igual
    /// al re-renderizar, videos distintos rotan. Público para tests.
    /// </summary>
    public static IReadOnlyList<ReelTrack> PickTracks(string? mood, string seed, int count, IReadOnlyList<ReelTrack>? library = null)
    {
        library ??= ReelMusicService.Library;
        var key = string.IsNullOrWhiteSpace(mood) ? "dark" : mood.Trim().ToLowerInvariant();
        var candidates = library.Where(t => t.Mood == key).ToList();
        if (candidates.Count == 0) candidates = [.. library];

        var hash = 0;
        foreach (var c in seed) hash = unchecked(hash * 31 + c);
        var start = Math.Abs(hash % candidates.Count);
        return Enumerable.Range(0, Math.Min(count, candidates.Count))
            .Select(i => candidates[(start + i) % candidates.Count]).ToList();
    }

    /// <summary>Une los tracks (cada uno desde su StartSeconds) con fundido cruzado. Público para tests.</summary>
    public static List<string> MusicBedArgs(IReadOnlyList<(string Path, int StartSeconds)> tracks, string output)
    {
        var args = new List<string> { "-y" };
        foreach (var (path, start) in tracks)
        {
            if (start > 0) args.AddRange(["-ss", start.ToString(CultureInfo.InvariantCulture)]);
            args.AddRange(["-i", path]);
        }
        var filter = new StringBuilder();
        for (var i = 0; i < tracks.Count; i++)
            filter.Append($"[{i}:a]aformat=sample_rates=48000:channel_layouts=stereo[t{i}];");
        var last = "t0";
        var d = CrossfadeSeconds.ToString(CultureInfo.InvariantCulture);
        for (var i = 1; i < tracks.Count; i++)
        {
            filter.Append($"[{last}][t{i}]acrossfade=d={d}[x{i}];");
            last = $"x{i}";
        }
        // La última etiqueta (o la única, con un solo track) pasa a ser la salida.
        args.AddRange(["-filter_complex", filter.ToString().TrimEnd(';').Replace($"[{last}]", "[out]"),
            "-map", "[out]", "-c:a", "pcm_s16le", output]);
        return args;
    }

    /// <summary>
    /// Filtro del audio final: la voz tal cual y la música (en loop) bajita,
    /// con ducking contra la voz y fundidos de entrada y salida. Público para tests.
    /// </summary>
    public static string MixFilter(double volume, double duration)
    {
        var inv = CultureInfo.InvariantCulture;
        var fadeOut = Math.Max(0, duration - 3).ToString("0.###", inv);
        return
            "[1:a]aformat=sample_rates=48000:channel_layouts=stereo,asplit=2[voice][key];" +
            $"[2:a]aformat=sample_rates=48000:channel_layouts=stereo,volume={volume.ToString("0.###", inv)}," +
            $"afade=t=in:d=2,afade=t=out:st={fadeOut}:d=3[music];" +
            // Ducking suave (E): con release corto la música subía en cada micropausa.
            "[music][key]sidechaincompress=threshold=0.02:ratio=3:attack=150:release=2000[ducked];" +
            "[voice][ducked]amix=inputs=2:duration=first:normalize=0[aout]";
    }
}
