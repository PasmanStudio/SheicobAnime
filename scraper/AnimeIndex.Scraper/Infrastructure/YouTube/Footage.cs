using System.Text.RegularExpressions;

namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>
/// Un video oficial que se puede usar como metraje (tráiler, teaser, PV, videoclip).
/// Va en <see cref="VideoPlan.Footage"/>: el manifiesto de fuentes del video. Cada
/// tramo de la narración muestra planos de la fuente que corresponde a lo que se
/// dice (ver <see cref="FootageRouter"/>): si se habla del videoclip, del videoclip;
/// si se habla de la temporada 2, de los tráileres de la temporada 2.
/// </summary>
public sealed class FootageSource
{
    /// <summary>Identificador corto y único ("s2-trailer", "mv-watson"). Es el nombre del archivo en footage/.</summary>
    public string Id { get; set; } = "";

    /// <summary>trailer | teaser | pv | mv | opening | ending | clip.</summary>
    public string Kind { get; set; } = "trailer";

    /// <summary>Temporada de la que es el material, si es de una (null = de la obra en general).</summary>
    public int? Season { get; set; }

    /// <summary>
    /// Video oficial (canal verificado de la serie, el estudio o la plataforma). Se
    /// baja con yt-dlp a footage/&lt;id&gt;.mp4 la primera vez: YouTube bloquea los
    /// runners, así que en la práctica se baja en local.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>Archivo ya bajado, relativo a la carpeta del video. Si falta, footage/&lt;id&gt;.*.</summary>
    public string? File { get; set; }

    /// <summary>Para el reporte: "Tráiler oficial T2 (Cyberpunk 2077)".</summary>
    public string? Label { get; set; }

    /// <summary>
    /// Palabras de la narración que hablan de ESTA fuente ("Watson", "Petite Meller",
    /// "Edgerunners Dos"). Sin tildes ni mayúsculas: se comparan como palabras.
    /// </summary>
    public List<string> Keywords { get; set; } = [];

    public bool IsMusicVideo => Kind is "mv" or "opening" or "ending";

    /// <summary>Id usable como nombre de archivo: minúsculas, dígitos y guiones. Público para tests.</summary>
    public static bool IsValidId(string id) => Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{0,40}$");

    /// <summary>Solo https:// (nada que yt-dlp pueda leer como opción o como archivo local). Público para tests.</summary>
    public static bool IsValidUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
}

/// <summary>A qué fuentes va una escena y por qué (para el reporte).</summary>
public sealed record FootageRoute(IReadOnlyList<string> Ids, string Reason);

/// <summary>
/// Elige, escena por escena, de qué videos sale el metraje. Sin IA, así un
/// re-render con --plan da siempre lo mismo:
///   1. <see cref="VideoScene.Footage"/> escrito en el plan manda (corrección a mano).
///   2. Si no, puntaje por pistas en la narración: palabras clave de cada fuente (2
///      por palabra), "videoclip"/"video musical" para los videoclips (3) y la
///      temporada nombrada ("segunda temporada", "temporada 2") para las fuentes de
///      esa temporada (1). Ganan todas las fuentes con el puntaje más alto.
///   3. Sin pistas, la escena sigue el tema de la anterior (los temas van en tramos).
///   4. La primera escena sin pistas va a la primera fuente del manifiesto.
/// </summary>
public static partial class FootageRouter
{
    private static readonly string[] MusicVideoCues = ["videoclip", "video clip", "video musical", "clip musical", "music video"];

    private static readonly Dictionary<string, int> Ordinals = new()
    {
        ["primera"] = 1, ["segunda"] = 2, ["tercera"] = 3, ["cuarta"] = 4, ["quinta"] = 5,
        ["uno"] = 1, ["dos"] = 2, ["tres"] = 3, ["cuatro"] = 4, ["cinco"] = 5,
    };

    /// <summary>Ruta de cada escena del plan. Público para tests.</summary>
    public static List<FootageRoute> Route(VideoPlan plan, IReadOnlyList<FootageSource> sources)
    {
        var routes = new List<FootageRoute>();
        var ids = sources.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        FootageRoute? previous = null;
        for (var i = 0; i < plan.Scenes.Count; i++)
        {
            var scene = plan.Scenes[i];
            FootageRoute route;
            var manual = (scene.Footage ?? []).Where(ids.Contains).ToList();
            if (manual.Count > 0)
                route = new(manual, "elegido en el plan");
            else if (sources.Count == 0)
                route = new([], "sin metraje");
            else if (Score(scene.Narration, sources) is { Count: > 0 } best)
                route = new(best.Select(b => b.Id).ToList(), string.Join("; ", best.Select(b => b.Why).Distinct()));
            else if (previous is not null)
                route = previous with { Reason = $"sigue el tema de la escena {i}" };
            else
                route = new([sources[0].Id], "primera fuente del manifiesto");
            routes.Add(route);
            previous = route;
        }
        return routes;
    }

    private static List<(string Id, string Why)> Score(string narration, IReadOnlyList<FootageSource> sources)
    {
        var text = Normalize(narration);
        var season = MentionedSeason(text);
        var scored = new List<(string Id, int Score, string Why)>();
        foreach (var s in sources)
        {
            var score = 0;
            var why = new List<string>();
            foreach (var k in s.Keywords)
                if (Contains(text, Normalize(k))) { score += 2; why.Add($"«{k}»"); }
            if (s.IsMusicVideo && MusicVideoCues.FirstOrDefault(c => Contains(text, c)) is { } cue) { score += 3; why.Add($"«{cue}»"); }
            if (season is not null && s.Season == season) { score++; why.Add($"temporada {season}"); }
            if (score > 0) scored.Add((s.Id, score, string.Join(" + ", why)));
        }
        if (scored.Count == 0) return [];
        var max = scored.Max(s => s.Score);
        return scored.Where(s => s.Score == max).Select(s => (s.Id, s.Why)).ToList();
    }

    /// <summary>"segunda temporada", "temporada 2", "temporada dos" → 2. Público para tests.</summary>
    public static int? MentionedSeason(string normalized)
    {
        var m = SeasonBefore().Match(normalized);
        if (m.Success && Ordinals.TryGetValue(m.Groups[1].Value, out var n)) return n;
        m = SeasonAfter().Match(normalized);
        if (!m.Success) return null;
        return int.TryParse(m.Groups[1].Value, out var d) ? d : Ordinals.GetValueOrDefault(m.Groups[1].Value) is > 0 and var o ? o : null;
    }

    /// <summary>Texto → palabras en minúsculas sin tildes ni signos, separadas por un espacio y con espacios en los bordes.</summary>
    public static string Normalize(string text) => " " + string.Join(' ', ShotTiming.Tokens(text)) + " ";

    private static bool Contains(string normalizedText, string phrase)
    {
        var p = phrase.Trim();
        return p.Length > 0 && normalizedText.Contains($" {p} ", StringComparison.Ordinal);
    }

    [GeneratedRegex(@" (primera|segunda|tercera|cuarta|quinta) temporada ")]
    private static partial Regex SeasonBefore();

    [GeneratedRegex(@" temporada (\d+|uno|dos|tres|cuatro|cinco) ")]
    private static partial Regex SeasonAfter();
}

/// <summary>Un plano de una fuente: tramo usable como clip y su fotograma central.</summary>
/// <param name="Moment">
/// Mismo número = mismo plano. Un teaser y un tráiler repiten planos: al usar uno,
/// el otro también queda usado.
/// </param>
public sealed record FootageSegment(double Start, double Length, string Still, double Time, int Moment);

/// <summary>Una fuente ya bajada y cortada en planos.</summary>
public sealed record FootagePool(string Id, string Kind, string Path, List<FootageSegment> Segments);

/// <summary>Plano elegido para una toma: de qué fuente, cuál, y si va como clip o como fotograma.</summary>
public sealed record FootagePick(FootagePool Pool, FootageSegment Segment, bool AsClip);

/// <summary>
/// Todas las fuentes del video y qué planos ya se usaron. Un plano no se repite
/// en todo el video mientras quede alguno sin usar (en ninguna fuente). Público
/// para tests: no toca archivos.
/// </summary>
public sealed class FootageLibrary(IReadOnlyList<FootagePool> pools)
{
    /// <summary>Dos tomas seguidas de una misma fuente, al menos a esta distancia (s).</summary>
    public const double MinDistance = 8;

    private readonly HashSet<int> _used = [];
    private readonly Dictionary<string, int> _turn = [];
    private readonly Dictionary<string, double> _last = [];
    private int _poolTurn;

    public IReadOnlyList<FootagePool> Pools { get; } = pools;

    /// <summary>Todos los fotogramas, en orden: lo que recibe CLIP.</summary>
    public IReadOnlyList<(FootagePool Pool, FootageSegment Segment)> Stills { get; } =
        pools.SelectMany(p => p.Segments.Select(s => (p, s))).ToList();

    public bool IsEmpty => Stills.Count == 0;

    /// <summary>
    /// El mejor plano según CLIP (<paramref name="ranked"/>: índice en <see cref="Stills"/>,
    /// puntaje, de mayor a menor) que sea de las fuentes de la escena y no se haya usado.
    /// Null si ninguno pasa <paramref name="minScore"/>.
    /// </summary>
    public FootagePick? PickMatched(IEnumerable<(int Index, float Score)> ranked, IReadOnlyCollection<string> group,
        float minScore, double duration)
    {
        foreach (var (idx, score) in ranked)
        {
            if (score < minScore) break;
            if (idx < 0 || idx >= Stills.Count) continue;
            var (pool, seg) = Stills[idx];
            if (group.Count > 0 && !group.Contains(pool.Id, StringComparer.OrdinalIgnoreCase)) continue;
            if (_used.Contains(seg.Moment)) continue;
            return Take(pool, seg, seg.Length >= duration * 0.6);
        }
        return null;
    }

    /// <summary>
    /// Relleno: el siguiente plano sin usar de las fuentes de la escena, rotando
    /// entre ellas y lejos del último plano de la misma fuente. Si esas fuentes ya
    /// no tienen planos nuevos: un plano nuevo de cualquier fuente; si no queda
    /// ninguno, se repite uno de las fuentes de la escena. Null si no hay metraje.
    /// </summary>
    public FootagePick? PickFiller(IReadOnlyCollection<string> group, bool preferClip, double duration)
    {
        var own = Pools.Where(p => group.Count == 0 || group.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (own.Count == 0) own = Pools.ToList();
        foreach (var (scope, allowUsed) in new[] { (own, false), (Pools.ToList(), false), (own, true) })
        {
            if (FindInRotation(scope, allowUsed) is { } found)
                return Take(found.Pool, found.Segment, preferClip && found.Segment.Length >= duration * 0.6);
        }
        return null;
    }

    private (FootagePool Pool, FootageSegment Segment)? FindInRotation(List<FootagePool> scope, bool allowUsed)
    {
        (FootagePool, FootageSegment)? near = null;
        for (var p = 0; p < scope.Count; p++)
        {
            var pool = scope[(_poolTurn + p) % scope.Count];
            var turn = _turn.GetValueOrDefault(pool.Id);
            for (var i = 0; i < pool.Segments.Count; i++)
            {
                var seg = pool.Segments[(turn + i) % pool.Segments.Count];
                if (!allowUsed && _used.Contains(seg.Moment)) continue;
                if (_last.TryGetValue(pool.Id, out var last) && Math.Abs(seg.Time - last) <= MinDistance)
                {
                    near ??= (pool, seg);
                    continue;
                }
                _poolTurn = (_poolTurn + p + 1) % scope.Count;
                return (pool, seg);
            }
        }
        if (near is not null) _poolTurn = (_poolTurn + 1) % scope.Count;
        return near;
    }

    private FootagePick Take(FootagePool pool, FootageSegment seg, bool asClip)
    {
        _used.Add(seg.Moment);
        _last[pool.Id] = seg.Time;
        _turn[pool.Id] = pool.Segments.IndexOf(seg) + 1;
        return new FootagePick(pool, seg, asClip);
    }
}
