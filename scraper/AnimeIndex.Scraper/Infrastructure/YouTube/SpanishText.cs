using System.Text.RegularExpressions;

namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>Palabra de la narración: tal cual se escribió, su posición y su índice (el mismo que <see cref="ShotTiming.Tokens"/>).</summary>
public sealed record NarrationWord(string Text, int Index, int Start, int End)
{
    public string Lower => ShotTiming.Tokens(Text).FirstOrDefault() ?? Text.ToLowerInvariant();
    public bool Capitalized => Text.Length > 0 && char.IsUpper(Text[0]);
}

/// <summary>Utilidades de texto en español para leer la narración: palabras y números dichos en letras.</summary>
public static partial class SpanishText
{
    /// <summary>Palabras de la narración con su posición; mismo corte que <see cref="ShotTiming.Tokens"/>.</summary>
    public static List<NarrationWord> Words(string text) =>
        WordRegex().Matches(text).Select((m, i) => new NarrationWord(m.Value, i, m.Index, m.Index + m.Length)).ToList();

    private static readonly Dictionary<string, long> Units = new()
    {
        ["cero"] = 0, ["un"] = 1, ["uno"] = 1, ["una"] = 1, ["dos"] = 2, ["tres"] = 3, ["cuatro"] = 4, ["cinco"] = 5,
        ["seis"] = 6, ["siete"] = 7, ["ocho"] = 8, ["nueve"] = 9, ["diez"] = 10, ["once"] = 11, ["doce"] = 12,
        ["trece"] = 13, ["catorce"] = 14, ["quince"] = 15, ["dieciseis"] = 16, ["diecisiete"] = 17,
        ["dieciocho"] = 18, ["diecinueve"] = 19, ["veinte"] = 20, ["veintiun"] = 21, ["veintiuno"] = 21, ["veintiuna"] = 21,
        ["veintidos"] = 22, ["veintitres"] = 23, ["veinticuatro"] = 24, ["veinticinco"] = 25, ["veintiseis"] = 26,
        ["veintisiete"] = 27, ["veintiocho"] = 28, ["veintinueve"] = 29, ["treinta"] = 30, ["cuarenta"] = 40,
        ["cincuenta"] = 50, ["sesenta"] = 60, ["setenta"] = 70, ["ochenta"] = 80, ["noventa"] = 90,
        ["cien"] = 100, ["ciento"] = 100, ["doscientos"] = 200, ["trescientos"] = 300, ["cuatrocientos"] = 400,
        ["quinientos"] = 500, ["seiscientos"] = 600, ["setecientos"] = 700, ["ochocientos"] = 800, ["novecientos"] = 900,
    };

    /// <summary>
    /// Número dicho en letras desde la palabra <paramref name="from"/> ("dos mil veintidós",
    /// "setenta y siete", "diez", también "2022"). Devuelve el valor y cuántas palabras ocupa,
    /// o null. "un"/"una" solos no cuentan (son artículos). Público para tests.
    /// </summary>
    public static (long Value, int Length)? NumberAt(IReadOnlyList<NarrationWord> words, int from)
    {
        if (from >= words.Count) return null;
        if (long.TryParse(words[from].Text, out var digits)) return (digits, 1);
        long total = 0, current = 0;
        var used = 0;
        var any = false;
        for (var i = from; i < words.Count; i++)
        {
            var w = words[i].Lower;
            if (Units.TryGetValue(w, out var v))
            {
                // Solo "ciento veinte" o "setenta y siete" siguen un número ya empezado; "dos dos" no.
                if (any && current != 0)
                {
                    var afterY = i - 1 > from && words[i - 1].Lower == "y";
                    if (!(current % 100 == 0 && v < 100) && !(afterY && v < 10)) break;
                }
                current += v;
                any = true;
                used = i - from + 1;
            }
            else if (w == "mil" && (any || i == from))
            {
                total += (current == 0 ? 1 : current) * 1000;
                current = 0;
                any = true;
                used = i - from + 1;
            }
            else if (w is "millon" or "millones" && any)
            {
                total += (current == 0 ? 1 : current) * 1_000_000;
                current = 0;
                used = i - from + 1;
            }
            else if (w == "y" && any && current % 10 == 0 && current is >= 30 and < 100 && i + 1 < words.Count
                     && Units.TryGetValue(words[i + 1].Lower, out var next) && next < 10)
                continue;
            else break;
        }
        if (!any || used == 0) return null;
        if (used == 1 && words[from].Lower is "un" or "una" or "uno") return null;
        return (total + current, used);
    }

    public static readonly string[] Months =
        ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    /// <summary>
    /// Palabras con mayúscula que NO son nombres aunque empiecen una oración o
    /// vayan después de dos puntos. Lo demás se decide mirando si la palabra
    /// aparece en minúscula en otro lado del guion.
    /// </summary>
    public static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "el", "la", "los", "las", "un", "una", "y", "o", "pero", "sin", "con", "en", "de", "del", "por", "para", "que",
        "es", "esta", "este", "esto", "estos", "estas", "ese", "esa", "eso", "su", "sus", "si", "no", "asi", "ahora",
        "ademas", "despues", "antes", "cuando", "como", "donde", "quien", "que", "cual", "el", "ella", "ellos", "nos",
        "muchos", "algunos", "vemos", "inicialmente", "finalmente", "mientras", "aunque", "tambien", "incluso", "hoy",
        "dejanos", "recuerdas", "fue", "era", "son", "hay", "a", "al", "lo", "le", "les", "mas", "muy", "ya", "aqui",
        "alli", "entonces", "luego", "primero", "segundo", "tercero", "sin embargo", "en cuanto", "vemos",
    };

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}]+")]
    private static partial Regex WordRegex();
}
