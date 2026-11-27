namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>Una variante de miniatura: qué personajes y qué gancho.</summary>
public sealed record ThumbnailVariant(string File, IReadOnlyList<string> Characters, string? Text);

/// <summary>
/// Decide las tres variantes para el "Test &amp; Compare" de YouTube (gana la que
/// suma más tiempo de visualización, no la de más clics). Cada una cambia UNA
/// cosa respecto de la A, para saber qué funcionó:
///   A: personajes + gancho principal  (thumbnail.jpg, la que sube la API)
///   B: mismos personajes + gancho alternativo
///   C: mismos personajes, sin texto
/// Las B y C se cargan a mano en Studio: la API no tiene el test.
/// </summary>
public static class ThumbnailPlanner
{
    public static List<ThumbnailVariant> Variants(VideoPlan plan)
    {
        var characters = Characters(plan);
        var text = Clean(plan.Thumbnail?.Text) ?? FallbackText(plan.Title);
        var alt = Clean(plan.Thumbnail?.Alternative);
        var variants = new List<ThumbnailVariant> { new("thumbnail.jpg", characters, text) };
        if (alt is not null && !alt.Equals(text, StringComparison.OrdinalIgnoreCase))
            variants.Add(new("thumbnail_b.jpg", characters, alt));
        if (text is not null)
            variants.Add(new("thumbnail_c.jpg", characters, null));
        return variants;
    }

    /// <summary>
    /// Los personajes del plan; en planes viejos, los 2 más nombrados en las tomas
    /// ("character: X"). Público para tests.
    /// </summary>
    public static List<string> Characters(VideoPlan plan)
    {
        if (plan.Thumbnail?.Characters is { Count: > 0 } chosen)
            return chosen.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct().Take(2).ToList();
        return plan.Scenes.SelectMany(s => s.Shots ?? [])
            .Select(s => ShotReference.Parse(s.Show))
            .Where(r => r.Kind == ShotKind.Character)
            .SelectMany(r => r.Names)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(2).Select(g => g.Key).ToList();
    }

    /// <summary>Hasta 4 palabras; null si no queda nada. Público para tests.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var words = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(4));
    }

    /// <summary>
    /// Sin gancho en el plan: la palabra que el título ya marca en mayúsculas
    /// ("El SECRETO de…" → "*SECRETO*"). Null si no hay. Público para tests.
    /// </summary>
    public static string? FallbackText(string title)
    {
        var caps = title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim(',', '.', ':', ';', '!', '?', '¿', '¡'))
            .FirstOrDefault(w => w.Length >= 4 && w.Any(char.IsLetter) && w == w.ToUpperInvariant());
        return caps is null ? null : $"*{caps}*";
    }
}
