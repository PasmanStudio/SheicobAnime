using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>Tipo de imagen de una toma, según lo que se nombra en ese momento.</summary>
public enum ShotKind
{
    /// <summary>Serie o temporada → póster oficial (AniList; si no es anime, la portada de Wikipedia).</summary>
    Poster,
    /// <summary>Estudio, empresa o plataforma → logo (infobox de Wikipedia). Varios con "+".</summary>
    Logo,
    /// <summary>Premio, cifra, fecha, dato → tarjeta generada con el texto.</summary>
    Card,
    /// <summary>Personaje con nombre → su imagen oficial (AniList). Varios con "+".</summary>
    Character,
    /// <summary>Persona real, lugar u obra que no es anime → la imagen de su artículo de Wikipedia.</summary>
    Photo,
    /// <summary>Descripción visual en inglés → el plano más parecido de los videos oficiales (CLIP).</summary>
    Scene,
    /// <summary>Idea abstracta sin imagen real → imagen generada.</summary>
    Ai,
    /// <summary>Arte oficial de la serie del video.</summary>
    KeyArt,
}

/// <summary>
/// "Show" de una toma ya interpretado. Card: Value = título, Detail = subtítulo,
/// Image = referencia de la imagen que acompaña ("poster: X", "character: Y").
/// Logo: Names = cada empresa.
/// </summary>
public sealed partial record ShotReference(ShotKind Kind, string Value, string? Detail = null, string? Image = null)
{
    public IReadOnlyList<string> Names =>
        Value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// "poster: X" / "logo: A + B" / "card: título | subtítulo | poster: X" /
    /// "character: X" / "scene: ..." / "ai: ..." / "keyart". Sin prefijo, se
    /// interpreta como hasta ahora: escena. Público para tests.
    /// </summary>
    public static ShotReference Parse(string show)
    {
        var s = show.Trim();
        if (s.Equals("keyart", StringComparison.OrdinalIgnoreCase)) return new(ShotKind.KeyArt, "");
        var m = Prefix().Match(s);
        if (!m.Success) return new(ShotKind.Scene, s);
        var rest = s[m.Length..].Trim();
        switch (m.Groups[1].Value.ToLowerInvariant())
        {
            case "poster": return new(ShotKind.Poster, rest);
            case "logo": return new(ShotKind.Logo, rest);
            case "character": return new(ShotKind.Character, rest);
            case "photo": return new(ShotKind.Photo, rest);
            case "ai": return new(ShotKind.Ai, rest);
            case "keyart": return new(ShotKind.KeyArt, rest);
            case "card":
                var parts = rest.Split('|', StringSplitOptions.TrimEntries);
                var image = parts.Skip(1).FirstOrDefault(p => Prefix().IsMatch(p));
                var texts = parts.Where(p => p != image && p.Length > 0).ToList();
                return new(ShotKind.Card, texts.FirstOrDefault() ?? "", texts.Count > 1 ? texts[1] : null, image);
            default: return new(ShotKind.Scene, rest);
        }
    }

    [GeneratedRegex(@"^(poster|logo|card|character|photo|scene|ai|keyart)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex Prefix();
}

/// <summary>
/// Cuándo se dice cada palabra y, con eso, cuándo empieza cada toma.
/// La fuente de los tiempos es faster-whisper sobre la voz ya generada
/// (Scripts/word_times.py); de whisper solo se usan los TIEMPOS: el texto es la
/// narración del plan, alineada palabra por palabra (whisper escribe "2022"
/// donde la narración dice "dos mil veintidós", o "Edgar Onnors" por
/// "Edgerunners": lo que no coincide se interpola entre vecinos que sí).
/// Sin whisper, los tiempos se estiman por la cantidad de letras.
/// </summary>
public static partial class ShotTiming
{
    /// <summary>La toma entra un poco antes de la palabra: el ojo llega con el oído.</summary>
    public const double LeadSeconds = 0.15;

    /// <summary>Una toma más corta que esto no se llega a ver: se descarta.</summary>
    public const double MinShotSeconds = 1.2;

    /// <summary>Más que esto quieta cansa: el resto de la toma pasa a relleno (clips del tráiler).</summary>
    public const double MaxHoldSeconds = 6.0;

    /// <summary>Palabras de un texto, en minúsculas, sin tildes ni signos. Público para tests.</summary>
    public static List<string> Tokens(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>
    /// Segundo en que empieza cada palabra de la narración, alineando con las
    /// palabras de whisper (texto, inicio). Alineación por programación dinámica
    /// con coincidencia aproximada; lo no alineado se interpola por letras.
    /// Público para tests.
    /// </summary>
    public static double[] WordStarts(string narration, IReadOnlyList<(string Word, double Start)> heard, double duration)
    {
        var words = Tokens(narration);
        var times = new double[words.Count];
        if (words.Count == 0) return times;

        var spoken = new List<(string Token, double Start)>();
        foreach (var (w, start) in heard)
            foreach (var t in Tokens(w)) spoken.Add((t, start));

        // LCS con coincidencia aproximada.
        int n = words.Count, m = spoken.Count;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                dp[i, j] = Similar(words[i], spoken[j].Token)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        var known = new double?[n];
        for (int i = 0, j = 0; i < n && j < m;)
        {
            if (Similar(words[i], spoken[j].Token) && dp[i, j] == dp[i + 1, j + 1] + 1) { known[i] = spoken[j].Start; i++; j++; }
            else if (dp[i + 1, j] >= dp[i, j + 1]) i++;
            else j++;
        }

        // Los tiempos tienen que crecer: un "match" que retrocede es un error de alineación.
        double last = -1;
        for (var i = 0; i < n; i++)
            if (known[i] is { } k) { if (k < last) known[i] = null; else last = k; }

        // Interpolación por letras entre anclas conocidas (bordes: 0 y la duración).
        var offsets = new double[n + 1];
        for (var i = 0; i < n; i++) offsets[i + 1] = offsets[i] + words[i].Length + 1;
        var prevIdx = -1; var prevTime = 0.0;
        for (var i = 0; i <= n; i++)
        {
            if (i < n && known[i] is null) continue;
            var nextTime = i < n ? known[i]!.Value : duration;
            var from = prevIdx < 0 ? 0 : offsets[prevIdx];
            for (var k = prevIdx + 1; k < i; k++)
            {
                var span = offsets[i] - from;
                times[k] = prevTime + (nextTime - prevTime) * (span <= 0 ? 0 : (offsets[k] - from) / span);
            }
            if (i < n) times[i] = nextTime;
            prevIdx = i; prevTime = nextTime;
        }
        return times;
    }

    /// <summary>
    /// Segundo (dentro de la escena) en que se dicen las palabras <paramref name="at"/>.
    /// Busca la secuencia de palabras desde <paramref name="fromWord"/>; si no la
    /// encuentra entera, la primera palabra; si tampoco, null. Público para tests.
    /// </summary>
    public static (double Time, int Word)? FindAnchor(string narration, double[] wordStarts, string at, int fromWord = 0)
    {
        var words = Tokens(narration);
        var target = Tokens(at);
        if (target.Count == 0 || words.Count == 0) return null;
        for (var len = target.Count; len >= 1; len--)
            for (var i = Math.Max(0, fromWord); i + len <= words.Count; i++)
                if (Enumerable.Range(0, len).All(k => words[i + k] == target[k]))
                    return (wordStarts[i], i);
        return null;
    }

    /// <summary>
    /// Tomas del video a partir de las anclas: <paramref name="anchors"/>[escena] =
    /// segundos (dentro de la escena) en que arranca cada toma de su lista, o null
    /// si no se encontró su ancla. La primera toma de cada escena arranca con la
    /// escena; las demás, en su ancla menos <see cref="LeadSeconds"/>. Una toma
    /// demasiado corta se descarta, y lo que pasa de <see cref="MaxHoldSeconds"/>
    /// se completa con relleno (Slot = -1). Cubre todo sin huecos. Público para tests.
    /// </summary>
    public static List<VideoShot> PlanAnchoredShots(IReadOnlyList<double> sceneSpans,
        IReadOnlyList<IReadOnlyList<double?>> anchors, double fillerSeconds = 3.5)
    {
        var shots = new List<VideoShot>();
        var sceneStart = 0.0;
        for (var s = 0; s < sceneSpans.Count; s++)
        {
            var span = sceneSpans[s];
            // (slot, inicio relativo), en orden y crecientes.
            var starts = new List<(int Slot, double At)>();
            var list = s < anchors.Count ? anchors[s] : [];
            for (var k = 0; k < list.Count; k++)
            {
                if (k == 0) { starts.Add((0, 0)); continue; }
                if (list[k] is not { } t) continue;
                var at = Math.Clamp(t - LeadSeconds, 0, span);
                if (starts.Count > 0 && at <= starts[^1].At) continue;
                starts.Add((k, at));
            }
            if (starts.Count == 0) starts.Add((list.Count > 0 ? 0 : -1, 0));

            // Descartar las que no llegan a verse: gana la mención posterior. Si la
            // corta es la primera de la escena, la siguiente arranca con la escena.
            for (var i = 0; i < starts.Count; i++)
            {
                var end = i + 1 < starts.Count ? starts[i + 1].At : span;
                if (end - starts[i].At >= MinShotSeconds || starts.Count == 1) continue;
                if (i == 0) starts[1] = (starts[1].Slot, 0);
                starts.RemoveAt(i);
                i = -1;
            }

            for (var i = 0; i < starts.Count; i++)
            {
                var a = starts[i].At;
                var b = i + 1 < starts.Count ? starts[i + 1].At : span;
                var d = b - a;
                if (d <= MaxHoldSeconds || starts[i].Slot < 0)
                {
                    if (starts[i].Slot < 0 && d > MaxHoldSeconds) AddFiller(shots, s, sceneStart + a, d, fillerSeconds);
                    else shots.Add(new VideoShot(s, starts[i].Slot, sceneStart + a, d));
                    continue;
                }
                var hold = Math.Min(fillerSeconds, d / 2);
                shots.Add(new VideoShot(s, starts[i].Slot, sceneStart + a, hold));
                AddFiller(shots, s, sceneStart + a + hold, d - hold, fillerSeconds);
            }
            sceneStart += span;
        }
        return shots;
    }

    private static void AddFiller(List<VideoShot> shots, int scene, double start, double length, double fillerSeconds)
    {
        var count = Math.Max(1, (int)Math.Round(length / fillerSeconds));
        var d = length / count;
        for (var j = 0; j < count; j++) shots.Add(new VideoShot(scene, -1, start + j * d, d));
    }

    private static bool Similar(string a, string b)
    {
        if (a == b) return true;
        if (a.Length >= 4 && b.Length >= 4 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)))
            return true;
        var max = Math.Max(a.Length, b.Length);
        return max >= 4 && Levenshtein(a, b) <= max / 3;
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
