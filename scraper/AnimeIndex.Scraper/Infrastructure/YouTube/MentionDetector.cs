using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.YouTube;

public enum CandidateKind { Name, Date, Count, Quote }

/// <summary>Algo que la narración nombra o afirma: dónde empieza (palabra) y qué es.</summary>
public sealed record Candidate(CandidateKind Kind, int Word, int Length, string Text)
{
    /// <summary>Año que sigue a un nombre ("Crunchyroll Anime Awards de dos mil veintitrés").</summary>
    public long? Year { get; init; }
}

/// <summary>
/// Reconoce SOLO, en cualquier guion, todo lo que se nombra o se afirma y le
/// asigna su imagen, anclada a las palabras donde se dice. No depende de que
/// Gemini lo haya marcado: corre sobre la narración final, completa las tomas
/// que Gemini dejó afuera y es lo único que hay cuando el plan no trae tomas.
///
///   • nombres propios (palabras con mayúscula que no empiezan una oración
///     por gramática; "Cyberpunk dos mil setenta y siete" → "Cyberpunk 2077")
///     → se clasifican con AniList y Wikipedia: serie → póster, personaje →
///     retrato, empresa → logo, persona / lugar / obra → foto, premio → placa;
///     lo que no se encuentra en ningún lado → placa con el nombre;
///   • fechas ("el veinte de octubre de dos mil veintiséis") → placa con la fecha;
///   • cifras ("diez episodios") → placa con la cifra;
///   • citas entre comillas de más de una palabra → placa con la cita.
/// Entre una mención y la siguiente, si pasan más de ~4 s, una toma de relleno
/// ("scene:" del storyboard o un plano del tráiler).
/// Deja mentions_report.md con todo lo encontrado, escena por escena.
/// </summary>
public sealed partial class MentionDetector(MentionImageService mentions, ILogger<MentionDetector> logger)
{
    /// <summary>Palabras (~4 s de voz) a partir de las cuales un hueco sin mención lleva una toma de relleno.</summary>
    public const int GapWords = 12;

    private static readonly HashSet<string> CountNouns = new(StringComparer.Ordinal)
    {
        "episodios", "capitulos", "temporadas", "volumenes", "tomos", "peliculas", "anos", "minutos", "horas",
        "millones", "personajes", "temporadas", "ovas", "especiales", "partes", "arcos", "copias", "premios", "dias", "meses",
    };

    // ── Candidatos (sin red: testeable) ─────────────────────────────────────

    /// <summary>
    /// Nombres, fechas, cifras y citas de una escena. <paramref name="lowercase"/> =
    /// palabras que en el guion aparecen en minúscula: una de esas con mayúscula al
    /// empezar una oración no es un nombre ("Esta", "Vemos"). Público para tests.
    /// </summary>
    public static List<Candidate> FindCandidates(string narration, ISet<string> lowercase)
    {
        var words = SpanishText.Words(narration);
        var found = new List<Candidate>();
        var used = new bool[words.Count];

        // Fechas: [día de] mes de año.
        for (var i = 0; i < words.Count; i++)
        {
            var month = Array.IndexOf(SpanishText.Months, words[i].Lower);
            if (month < 0) continue;
            var start = i;
            long? day = null;
            if (i >= 2 && words[i - 1].Lower == "de")
                for (var s = Math.Max(0, i - 4); s < i - 1; s++)
                    if (SpanishText.NumberAt(words, s) is { } d && s + d.Length == i - 1 && d.Value is >= 1 and <= 31) { day = d.Value; start = s; break; }
            long? year = null;
            var end = i + 1;
            if (i + 2 < words.Count && words[i + 1].Lower == "de" && SpanishText.NumberAt(words, i + 2) is { } y && y.Value is >= 1900 and <= 2100)
            {
                year = y.Value;
                end = i + 2 + y.Length;
            }
            if (day is null && year is null) continue;
            var name = char.ToUpperInvariant(SpanishText.Months[month][0]) + SpanishText.Months[month][1..];
            var text = day is not null
                ? $"{day} de {SpanishText.Months[month]}" + (year is not null ? $" de {year}" : "")
                : $"{name} de {year}";
            found.Add(new Candidate(CandidateKind.Date, start, end - start, text));
            for (var k = start; k < end; k++) used[k] = true;
        }

        // Nombres propios (con números pegados: "Cyberpunk 2077", "Edgerunners Dos").
        for (var i = 0; i < words.Count; i++)
        {
            if (used[i] || !IsNameWord(narration, words, i, lowercase)) continue;
            var parts = new List<string> { words[i].Text };
            var j = i;
            while (j + 1 < words.Count && !used[j + 1] && Joined(narration, words[j], words[j + 1]))
            {
                var next = words[j + 1];
                if (SpanishText.NumberAt(words, j + 1) is { } n && (next.Capitalized || n.Value >= 2 && !IsCountNoun(words, j + 1 + n.Length)))
                {
                    // Solo si el número es parte del nombre: pegado y sin "de" antes.
                    parts.Add(n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    j += n.Length;
                    break;
                }
                if (next.Capitalized && !SpanishText.Stop.Contains(next.Lower)) { parts.Add(Separator(narration, words[j], next) + next.Text); j++; continue; }
                if (next.Lower is "de" or "del" && j + 2 < words.Count && words[j + 2].Capitalized && !SpanishText.Stop.Contains(words[j + 2].Lower)
                    && Joined(narration, next, words[j + 2]))
                {
                    parts.Add(" " + next.Text + " " + words[j + 2].Text);
                    j += 2;
                    continue;
                }
                break;
            }
            var text = parts[0] + string.Concat(parts.Skip(1).Select(p => p.StartsWith(' ') || p.StartsWith(':') ? p : " " + p));
            var candidate = new Candidate(CandidateKind.Name, i, j - i + 1, text);
            // "… Awards de dos mil veintitrés": el año va con el nombre.
            if (j + 2 < words.Count && words[j + 1].Lower == "de" && SpanishText.NumberAt(words, j + 2) is { } yr && yr.Value is >= 1900 and <= 2100)
            {
                candidate = candidate with { Year = yr.Value };
                for (var k = j + 1; k < j + 2 + yr.Length; k++) used[k] = true;
            }
            found.Add(candidate);
            for (var k = i; k <= j; k++) used[k] = true;
        }

        // Cifras: "diez episodios", "tres temporadas", "dos millones de copias".
        for (var i = 0; i < words.Count; i++)
        {
            if (used[i] || SpanishText.NumberAt(words, i) is not { } n || n.Value < 2) continue;
            var nounAt = i + n.Length;
            if (!IsCountNoun(words, nounAt)) continue;
            var noun = words[nounAt].Text;
            var len = n.Length + 1;
            if (words[nounAt].Lower == "millones" && nounAt + 2 < words.Count && words[nounAt + 1].Lower == "de")
            {
                noun += " de " + words[nounAt + 2].Text;
                len += 2;
            }
            found.Add(new Candidate(CandidateKind.Count, i, len, $"{n.Value} {noun}"));
            for (var k = i; k < i + len; k++) used[k] = true;
        }

        // Citas de más de una palabra o en minúscula ('corporate insider'); un nombre solo
        // entre comillas ('Watson', 'King') ya quedó como nombre.
        foreach (Match q in QuoteRegex().Matches(narration))
        {
            var inner = q.Groups[1].Value.Trim();
            var qWords = SpanishText.Words(inner);
            if (qWords.Count == 0 || qWords.Count > 14 || qWords.Count == 1 && qWords[0].Capitalized) continue;
            var first = words.FindIndex(w => w.Start >= q.Index);
            if (first < 0) continue;
            found.Add(new Candidate(CandidateKind.Quote, first, qWords.Count, inner));
        }

        return found.OrderBy(c => c.Word).ToList();
    }

    private static bool IsCountNoun(IReadOnlyList<NarrationWord> words, int i) =>
        i < words.Count && CountNouns.Contains(words[i].Lower);

    private static bool IsNameWord(string text, IReadOnlyList<NarrationWord> words, int i, ISet<string> lowercase)
    {
        var w = words[i];
        if (!w.Capitalized || SpanishText.Stop.Contains(w.Lower)) return false;
        if (SpanishText.NumberAt(words, i) is not null && !(i > 0 && words[i - 1].Capitalized)) return false;
        // Mayúscula por gramática (empieza la oración, después de dos puntos o de comillas):
        // es un nombre solo si esa palabra no aparece en minúscula en el guion.
        var before = text[..w.Start].TrimEnd();
        var grammatical = before.Length == 0 || ".!?¿¡:".Contains(before[^1]) || (before[^1] is '\'' or '"' or '“' && (before.Length == 1 || ".!?¿¡: ".Contains(before[^2])));
        return !grammatical || !lowercase.Contains(w.Lower);
    }

    private static string Separator(string text, NarrationWord a, NarrationWord b) => text[a.End..b.Start];

    /// <summary>Dos palabras van juntas en un nombre si entre ellas solo hay espacio, dos puntos o guion.</summary>
    private static bool Joined(string text, NarrationWord a, NarrationWord b) =>
        Regex.IsMatch(Separator(text, a, b), @"^(?:\s+|:\s+|\s*-\s*)$");

    [GeneratedRegex(@"['""“‘]([^'""”’]{2,120})['""”’]")]
    private static partial Regex QuoteRegex();

    // ── Plan completo (con red) ─────────────────────────────────────────────

    /// <summary>
    /// Marca en el plan todas las menciones con su toma. Respeta las tomas que ya
    /// trae (Gemini) y agrega las que faltan; devuelve el informe.
    /// </summary>
    public async Task<string> AnnotateAsync(VideoPlan plan, string? anime, string dir, CancellationToken ct)
    {
        var lowercase = new HashSet<string>(plan.Scenes
            .SelectMany(s => SpanishText.Words(s.Narration)).Where(w => !w.Capitalized).Select(w => w.Lower));
        var perScene = plan.Scenes.Select(s => FindCandidates(s.Narration, lowercase)).ToList();

        // 1. Qué es cada nombre. Primero las series (para saber de qué personajes hablamos).
        var names = perScene.SelectMany(c => c).Where(c => c.Kind == CandidateKind.Name).Select(c => c.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var characters = new List<string>();
        if (anime is not null) characters.AddRange(await mentions.SeriesCharactersAsync(anime, ct));
        foreach (var name in names.Where(n => n.Contains(' ') || n.Any(char.IsDigit)))
        {
            kinds[name] = await mentions.ClassifyAsync(name, dir, ct);
            if (kinds[name] == "series") characters.AddRange(await mentions.SeriesCharactersAsync(name, ct));
        }
        characters = characters.Distinct().ToList();
        foreach (var name in names.Where(n => !kinds.ContainsKey(n)))
            kinds[name] = CharacterNamed(name, characters) is not null ? "character" : await mentions.ClassifyAsync(name, dir, ct);

        // 2. Tomas por escena.
        var report = new StringBuilder("# Menciones reconocidas\n\n");
        var lastPlate = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastPortrait = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastSeries = anime;
        var unresolved = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        for (var s = 0; s < plan.Scenes.Count; s++)
        {
            var scene = plan.Scenes[s];
            var words = SpanishText.Words(scene.Narration);
            var detected = new List<(int Word, string Show, string Why)>();
            // Las placas (fecha, cifra, premio) llevan el póster de la serie de la que se viene hablando.
            var poster = perScene[s].Where(c => c.Kind == CandidateKind.Name && kinds[c.Text] == "series").Select(c => c.Text).FirstOrDefault() ?? lastSeries;
            lastSeries = perScene[s].Where(c => c.Kind == CandidateKind.Name && kinds[c.Text] == "series").Select(c => c.Text).LastOrDefault() ?? lastSeries;
            var consumed = new HashSet<Candidate>();
            foreach (var c in perScene[s])
            {
                if (consumed.Contains(c)) continue;
                switch (c.Kind)
                {
                    case CandidateKind.Date:
                        detected.Add((c.Word, Card(c.Text, null, poster), "fecha"));
                        break;
                    case CandidateKind.Count:
                        detected.Add((c.Word, Card(c.Text, null, poster), "cifra"));
                        break;
                    case CandidateKind.Quote:
                        detected.Add((c.Word, Card($"“{c.Text}”", null, null), "cita"));
                        break;
                    case CandidateKind.Name:
                        var kind = kinds[c.Text];
                        var isAward = kind == "award" || AwardWord().IsMatch(c.Text);
                        if (isAward)
                        {
                            // La categoría es el nombre sin clasificar justo antes ("Anime del Año").
                            var category = perScene[s].LastOrDefault(o => o.Kind == CandidateKind.Name && o.Word < c.Word
                                && c.Word - (o.Word + o.Length) <= 4 && kinds[o.Text] == "");
                            var detail = c.Text + (c.Year is { } y ? $" {y}" : "");
                            if (category is not null)
                            {
                                consumed.Add(category);
                                detected.RemoveAll(d => d.Word == category.Word);
                                detected.Add((category.Word, Card(category.Text, detail, poster), "premio"));
                            }
                            else detected.Add((c.Word, Card(detail, null, poster), "premio"));
                            break;
                        }
                        var show = kind switch
                        {
                            "series" => $"poster: {c.Text}",
                            "character" => $"character: {CharacterNamed(c.Text, characters) ?? c.Text}",
                            "company" => $"logo: {c.Text}",
                            "work" => $"poster: {c.Text}",
                            "person" or "place" => $"photo: {c.Text}",
                            _ => null,
                        };
                        if (kind == "character")
                        {
                            // El mismo retrato una y otra vez cansa: se repite recién a las 3 escenas
                            // (mientras tanto van planos del tráiler, que suelen mostrarlo igual).
                            var who = CharacterNamed(c.Text, characters) ?? c.Text;
                            if (lastPortrait.TryGetValue(who, out var seen) && seen != s && s - seen < 3) break;
                            lastPortrait[who] = s;
                        }
                        if (show is null)
                        {
                            unresolved.Add(c.Text);
                            // Un nombre sin imagen: placa con el nombre, la primera vez y cada 5 escenas.
                            if (lastPlate.TryGetValue(c.Text, out var last) && s - last < 5) break;
                            lastPlate[c.Text] = s;
                            show = Card(c.Text + (c.Year is { } yy ? $" {yy}" : ""), null, null);
                        }
                        detected.Add((c.Word, show, kind.Length > 0 ? kind : "sin clasificar"));
                        break;
                }
            }

            var shots = MergeShots(scene, words, detected.OrderBy(d => d.Word).ToList());
            added += shots.Count - (scene.Shots?.Count ?? 0);
            scene.Shots = shots;

            report.Append($"## Escena {s + 1}\n\n> {scene.Narration}\n\n");
            foreach (var d in detected.OrderBy(d => d.Word))
                report.Append($"- «{At(words, d.Word)}» → `{d.Show}` ({d.Why})\n");
            if (detected.Count == 0) report.Append("- (sin menciones: tomas de relleno)\n");
            report.Append('\n');
        }
        report.Append("## Nombres que no se encontraron en AniList ni en Wikipedia (van como placa con el nombre)\n\n")
              .Append(unresolved.Count == 0 ? "- ninguno\n" : string.Join("\n", unresolved.Select(u => $"- {u}")) + "\n");
        report.Insert("# Menciones reconocidas\n\n".Length,
            $"Nombres: {names.Count} ({string.Join(", ", kinds.GroupBy(k => k.Value.Length > 0 ? k.Value : "sin clasificar").Select(g => $"{g.Key} {g.Count()}"))}). Tomas agregadas: {added}.\n\n");

        await File.WriteAllTextAsync(Path.Combine(dir, "mentions_report.md"), report.ToString(), ct);
        logger.LogInformation("Menciones: {Names} nombres, {Added} tomas agregadas, {Unresolved} sin imagen — ver mentions_report.md",
            names.Count, added, unresolved.Count);
        return report.ToString();
    }

    /// <summary>
    /// "Avida" → "Avida Yang" si es el nombre o el apellido de un personaje de la
    /// serie (y no hay dos que lo compartan). Nombre completo → el mismo. Público para tests.
    /// </summary>
    public static string? CharacterNamed(string name, IReadOnlyList<string> characters)
    {
        var wanted = ShotTiming.Tokens(name);
        if (wanted.Count == 0) return null;
        var full = characters.FirstOrDefault(c => ShotTiming.Tokens(c).SequenceEqual(wanted));
        if (full is not null) return full;
        if (wanted.Count != 1 || wanted[0].Length < 3) return null;
        var matches = characters.Where(c => ShotTiming.Tokens(c).Contains(wanted[0])).Distinct().ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string Card(string title, string? detail, string? poster) =>
        "card: " + title + (detail is null ? "" : " | " + detail) + (poster is null ? "" : " | poster: " + poster);

    private static string At(IReadOnlyList<NarrationWord> words, int i) =>
        string.Join(' ', words.Skip(i).Take(Math.Min(3, words.Count - i)).Select(w => w.Text));

    /// <summary>
    /// Une las tomas que ya trae la escena (Gemini) con las menciones detectadas:
    /// una mención que Gemini no cubrió se agrega; si cae a 2 palabras o menos de
    /// una toma "scene:" de Gemini, la reemplaza. Menciones del mismo tipo a menos
    /// de 4 palabras (logos o personajes nombrados juntos) van en una sola toma
    /// con "+". Después, relleno en los huecos de más de <see cref="GapWords"/>
    /// palabras. Público para tests.
    /// </summary>
    public static List<VideoShotSpec> MergeShots(VideoScene scene, IReadOnlyList<NarrationWord> words,
        IReadOnlyList<(int Word, string Show, string Why)> detected)
    {
        var uniform = new double[words.Count];
        var list = new List<(int Word, string Show, bool Mention)>();
        var from = 0;
        foreach (var shot in scene.Shots ?? [])
        {
            var at = ShotTiming.FindAnchor(scene.Narration, uniform, shot.At, from);
            if (at is null) continue;
            from = at.Value.Word;
            list.Add((at.Value.Word, shot.Show, ShotReference.Parse(shot.Show).Kind != ShotKind.Scene));
        }
        foreach (var (word, show, _) in detected)
        {
            if (list.Any(l => Same(l.Show, show))) continue;
            list.RemoveAll(l => !l.Mention && Math.Abs(l.Word - word) <= 2 && l.Word != 0);
            list.Add((word, show, true));
        }
        list = list.OrderBy(l => l.Word).ToList();

        // Logos o personajes nombrados juntos → una sola toma.
        for (var i = 1; i < list.Count; i++)
        {
            var (a, b) = (ShotReference.Parse(list[i - 1].Show), ShotReference.Parse(list[i].Show));
            if (a.Kind == b.Kind && a.Kind is ShotKind.Logo or ShotKind.Character && list[i].Word - list[i - 1].Word <= 4)
            {
                var prefix = a.Kind == ShotKind.Logo ? "logo: " : "character: ";
                list[i - 1] = (list[i - 1].Word, prefix + string.Join(" + ", a.Names.Concat(b.Names).Distinct()), true);
                list.RemoveAt(i--);
            }
        }

        // Relleno: al principio de la escena y en los huecos largos, en el comienzo de una frase.
        // De las descripciones viejas sirven solo las visuales: CLIP no sabe quién es
        // "Weak Kingsley" ni cómo es un "logo"; eso ya lo cubren las menciones.
        var fillers = new Queue<string>((scene.VisualQueries ?? []).Where(IsVisualDescription).Select(q => "scene: " + q));
        string Filler() => fillers.Count > 0 ? fillers.Dequeue() : "scene: ";
        // Una mención en las primeras palabras arranca con la escena (un relleno de
        // medio segundo antes no se llega a ver).
        if (list.Count > 0 && list[0].Word <= 4) list[0] = (0, list[0].Show, list[0].Mention);
        else if (list.Count == 0 || list[0].Word > 0) list.Insert(0, (0, Filler(), false));
        for (var i = 0; i < list.Count; i++)
        {
            var end = i + 1 < list.Count ? list[i + 1].Word : words.Count;
            if (end - list[i].Word <= GapWords) continue;
            var mid = (list[i].Word + end) / 2;
            var cut = Enumerable.Range(list[i].Word + 4, Math.Max(0, end - list[i].Word - 6))
                .Where(k => k > 0 && ClauseStart(scene.Narration, words, k))
                .OrderBy(k => Math.Abs(k - mid)).Cast<int?>().FirstOrDefault() ?? mid;
            list.Insert(i + 1, (cut, Filler(), false));
        }
        return list.Select(l => new VideoShotSpec { At = At(words, l.Word), Show = l.Show }).ToList();
    }

    /// <summary>"a red rose on the ground" sí; "CD Projekt Red logo" o "Weak Kingsley embracing Avida" no. Público para tests.</summary>
    public static bool IsVisualDescription(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 3
            && !words.Skip(1).Any(w => w.TrimStart('\'', '"') is { Length: > 0 } t && char.IsUpper(t[0]))
            && !Regex.IsMatch(query, @"\b(logo|cover|poster|title card|screenshot)\b", RegexOptions.IgnoreCase);
    }

    private static bool ClauseStart(string text, IReadOnlyList<NarrationWord> words, int k) =>
        text[words[k - 1].End..words[k].Start].IndexOfAny([',', '.', ';', ':', '?', '!']) >= 0;

    private static bool Same(string a, string b)
    {
        var (x, y) = (ShotReference.Parse(a), ShotReference.Parse(b));
        return x.Kind == y.Kind && string.Concat(ShotTiming.Tokens(x.Value)) == string.Concat(ShotTiming.Tokens(y.Value));
    }

    [GeneratedRegex(@"\b(awards?|premios?|prize)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AwardWord();
}
