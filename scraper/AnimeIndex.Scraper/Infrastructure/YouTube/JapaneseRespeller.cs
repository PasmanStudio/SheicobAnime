using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>
/// Romaji (Hepburn) → ortografía española, para que una voz sintética en
/// español diga bien los nombres japoneses. Reglas fijas y en orden, sacadas
/// de una investigación de fonología (26-nov-2026):
///
///   • El japonés no tiene acento de intensidad (es tonal): se usa el acento
///     por defecto del español, que para palabras terminadas en vocal o n cae
///     en la penúltima — así que casi nunca hace falta tilde. "Shinguéki" con
///     tilde sonaba forzado; "Shingueki" ya se acentúa ahí solo.
///   • j japonesa [dʑ] ≈ "y" latina; h japonesa [h] ≈ "j" (la h española es muda);
///     ge/gi con g dura → gue/gui; ky/ry/… → ki/ri/…; ny → ñ.
///   • Vocales largas y consonantes dobles se simplifican (Sousou → Soso,
///     Shippuden → Shipuden): la diferencia no existe para un oído hispano.
///   • La r inicial queda (el español la hace vibrante; no hay forma de
///     escribir una r simple al inicio).
/// Las excepciones (Tokio, nombres en inglés, etc.) van en el glosario, que
/// tiene prioridad sobre estas reglas.
/// </summary>
public static partial class JapaneseRespeller
{
    public static string Respell(string romaji)
    {
        // 1. Diacríticos y puntuación: ō→o, guiones y dos puntos → espacio.
        var text = RemoveDiacritics(romaji).Replace('-', ' ').Replace(':', ' ');
        text = MultiSpace().Replace(text, " ").Trim();

        var words = text.Split(' ').Select(RespellWord);
        return string.Join(' ', words);
    }

    /// <summary>
    /// Palabras que el oído eligió distinto de las reglas (prueba A/B con la voz
    /// clonada, 26-nov-2026). Van por palabra para que valgan también dentro de
    /// nombres compuestos ("Tanjiro Kamado"). Con la j inicial de "Jujutsu" la
    /// "dy" (africada, más cerca de [dʑ]) sonó mejor que la "y"; "Tanjiro" tal
    /// cual sonó mejor que "Tanyiro".
    /// </summary>
    private static readonly Dictionary<string, string> WordExceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jujutsu"] = "dyudyutsu",
        ["tanjiro"] = "tanjiro",
    };

    private static string RespellWord(string word)
    {
        if (word.Length == 0) return word;
        var capitalized = char.IsUpper(word[0]);
        var allCaps = word.Length > 1 && word.All(c => !char.IsLetter(c) || char.IsUpper(c));
        var w = word.ToLowerInvariant();

        if (WordExceptions.TryGetValue(w, out var exception))
        {
            if (allCaps) return exception.ToUpperInvariant();
            return capitalized ? char.ToUpperInvariant(exception[0]) + exception[1..] : exception;
        }

        // n' marca límite de mora (Shin'ya): que no se lea "ñ".
        w = w.Replace("n'", "n\u0001");

        // 2. Vocales largas (ei se deja: el "ei" español alcanza).
        w = w.Replace("ou", "o").Replace("oo", "o").Replace("uu", "u")
             .Replace("aa", "a").Replace("ii", "i").Replace("ee", "e");

        // 3. Consonantes dobles.
        w = w.Replace("tch", "ch").Replace("kk", "k").Replace("tt", "t").Replace("pp", "p").Replace("ss", "s");

        // 4. n ante b/p/m suena m.
        w = NBeforeLabial().Replace(w, "m$1");

        // 5. Palatales del romaji ORIGINAL, antes de j → y: si no, "Tanjiro" daba
        // "tanyiro" y después ny → ñ lo volvía "Tañiro" (lo atrapó un test).
        w = GyVowel().Replace(w, "gui$1");
        w = w.Replace("ky", "ki").Replace("ry", "ri").Replace("my", "mi")
             .Replace("by", "bi").Replace("py", "pi").Replace("ny", "ñ");

        // 6. j → y (antes de convertir h → j en el paso 8).
        w = w.Replace('j', 'y');

        // 7. g dura ante e/i.
        w = w.Replace("ge", "gue").Replace("gi", "gui");

        // 8. hy → ji y h sola → j (no la de sh ni ch).
        w = w.Replace("hy", "ji");
        w = StandaloneH().Replace(w, "j");

        w = w.Replace("\u0001", "");

        // Sin tildes automáticas: dónde cae el acento en "Gyomei" o "sensei" es
        // cuestión de uso, no de reglas. Los casos que lo necesitan van al glosario.

        if (allCaps) return w.ToUpperInvariant();
        return capitalized ? char.ToUpperInvariant(w[0]) + w[1..] : w;
    }

    private static string RemoveDiacritics(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"\s+")] private static partial Regex MultiSpace();
    [GeneratedRegex(@"n([bpm])")] private static partial Regex NBeforeLabial();
    [GeneratedRegex(@"gy([aeiou])")] private static partial Regex GyVowel();
    [GeneratedRegex(@"(?<![sc])h")] private static partial Regex StandaloneH();
}
