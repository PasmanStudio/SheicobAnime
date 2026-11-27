using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using AnimeIndex.Scraper.Infrastructure.Instagram;

namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>
/// Credenciales y opciones de la subida al canal (sección "YouTube" de la
/// configuración: YouTube__ClientId, YouTube__ClientSecret, YouTube__RefreshToken).
/// Los valores llegan de los secrets de GitHub; nunca van al repo (es público).
/// </summary>
public sealed class YouTubeSettings
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RefreshToken { get; set; } = "";

    /// <summary>
    /// private | unlisted | public. Default privado: sin la auditoría de YouTube
    /// API Services la API fuerza privado igual, y además así se revisa en Studio
    /// antes de publicar.
    /// </summary>
    public string PrivacyStatus { get; set; } = "private";

    public string SiteUrl { get; set; } = "https://sheicobanime.sheicob.workers.dev";

    /// <summary>@ de Instagram para la descripción; vacío = la línea no va.</summary>
    public string InstagramHandle { get; set; } = "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(RefreshToken);
}

/// <summary>Cuerpo de <c>videos.insert</c> (partes snippet y status).</summary>
public sealed record YouTubeVideoResource(
    [property: JsonPropertyName("snippet")] YouTubeSnippet Snippet,
    [property: JsonPropertyName("status")] YouTubeStatus Status);

public sealed record YouTubeSnippet(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("categoryId")] string CategoryId,
    [property: JsonPropertyName("defaultLanguage")] string DefaultLanguage,
    [property: JsonPropertyName("defaultAudioLanguage")] string DefaultAudioLanguage);

public sealed record YouTubeStatus(
    [property: JsonPropertyName("privacyStatus")] string PrivacyStatus,
    [property: JsonPropertyName("selfDeclaredMadeForKids")] bool SelfDeclaredMadeForKids,
    [property: JsonPropertyName("containsSyntheticMedia")] bool ContainsSyntheticMedia);

/// <summary>
/// Arma el título, la descripción (plantilla del canal) y los tags de la subida
/// a partir del plan.json. Todo puro y público para tests.
/// </summary>
public static class YouTubeMetadata
{
    /// <summary>Entretenimiento.</summary>
    public const string CategoryId = "24";
    public const string Language = "es-419";

    public const int MaxTitleLength = 100;
    /// <summary>El límite de la API es 5000 bytes en UTF-8.</summary>
    public const int MaxDescriptionBytes = 5000;
    /// <summary>Suma de los tags (los que tienen espacios cuentan las comillas) más una coma entre cada uno.</summary>
    public const int MaxTagsLength = 500;

    public static readonly string[] Hashtags = ["#anime", "#noticiasanime", "#teoriasanime"];

    /// <summary>Playlists del canal (se buscan por título con playlists.list, sin IDs en el repo).</summary>
    public const string PlaylistNews = "Noticias de anime";
    public const string PlaylistTheories = "Teorías de anime";
    public const string PlaylistAnalysis = "Análisis y explicaciones";
    public const string PlaylistSeason = "Anime de temporada";

    public static YouTubeVideoResource Build(VideoPlan plan, YouTubeSettings settings, IReadOnlyList<ReelTrack> music) =>
        new(
            new YouTubeSnippet(
                Title(plan.Title),
                Description(plan, settings, music),
                Tags(plan.Tags),
                CategoryId, Language, Language),
            new YouTubeStatus(NormalizePrivacy(settings.PrivacyStatus), SelfDeclaredMadeForKids: false, ContainsSyntheticMedia: true));

    /// <summary>YouTube rechaza "&lt;" y "&gt;" en título y descripción, y el título vacío o de más de 100 caracteres.</summary>
    public static string Title(string title)
    {
        var t = Clean(title).ReplaceLineEndings(" ").Trim();
        if (t.Length == 0) throw new InvalidOperationException("El plan no tiene título");
        if (t.Length <= MaxTitleLength) return t;
        var cut = t[..MaxTitleLength];
        var space = cut.LastIndexOf(' ');
        return (space > MaxTitleLength / 2 ? cut[..space] : cut).TrimEnd(' ', ',', ':', '-', '—');
    }

    /// <summary>
    /// Plantilla de subida del canal: resumen, sitio, Instagram, fuentes, crédito
    /// CC BY de cada tema de Kevin MacLeod (la licencia lo exige), aviso de IA y
    /// hashtags. Si se pasa de 5000 bytes se recorta el resumen, nunca los créditos.
    /// </summary>
    public static string Description(VideoPlan plan, YouTubeSettings settings, IReadOnlyList<ReelTrack> music)
    {
        string Compose(string summary)
        {
            var sb = new StringBuilder();
            if (summary.Length > 0) sb.Append(summary).Append("\n\n");
            sb.Append("📺 Episodios y calendario de la temporada: ").Append(settings.SiteUrl).Append('\n');
            var ig = settings.InstagramHandle.Trim().TrimStart('@');
            if (ig.Length > 0) sb.Append("📸 Instagram: @").Append(ig).Append('\n');

            var sources = plan.Sources.Select(s => Clean(s).Trim()).Where(s => s.Length > 0).Distinct().ToList();
            if (sources.Count > 0)
            {
                sb.Append("\nFuentes:\n");
                foreach (var s in sources) sb.Append("- ").Append(s).Append('\n');
            }

            var credited = music.Where(t => t.Attribution is not null).ToList();
            if (credited.Count > 0)
            {
                sb.Append("\nMúsica:\n");
                foreach (var t in credited)
                    sb.Append('"').Append(t.Title).Append("\" Kevin MacLeod (incompetech.com)\n");
                sb.Append("Licensed under Creative Commons: By Attribution 4.0 License\n")
                  .Append("http://creativecommons.org/licenses/by/4.0/\n");
            }

            sb.Append("\nNarración con voz sintética e imágenes generadas con IA.\n\n")
              .Append(string.Join(' ', Hashtags));
            return sb.ToString();
        }

        var summary = Clean(plan.Description).Trim();
        var full = Compose(summary);
        var excess = Encoding.UTF8.GetByteCount(full) - MaxDescriptionBytes;
        if (excess <= 0) return full;

        // Recortar el resumen por caracteres de texto (no por bytes a ciegas: no partir un emoji).
        var info = new StringInfo(summary);
        var keep = info.LengthInTextElements;
        while (keep > 0 && Encoding.UTF8.GetByteCount(Compose(info.SubstringByTextElements(0, keep) + "…")) > MaxDescriptionBytes)
            keep -= Math.Max(1, (keep / 20));
        return Compose(keep > 0 ? info.SubstringByTextElements(0, keep).TrimEnd() + "…" : "");
    }

    /// <summary>Sin duplicados (sin importar mayúsculas), sin "&lt;&gt;" ni comas, y dentro de los 500 caracteres.</summary>
    public static IReadOnlyList<string> Tags(IEnumerable<string> tags)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string channel = "SheicobAnime";
        // El tag del canal va siempre: se le reserva el lugar desde el principio.
        var reserved = true;
        var used = channel.Length + 1;
        foreach (var raw in tags.Append(channel))
        {
            var tag = Clean(raw).Replace(",", " ").Replace('"', ' ').Trim().TrimStart('#');
            tag = string.Join(' ', tag.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (tag.Length == 0 || !seen.Add(tag)) continue;
            if (reserved && tag.Equals(channel, StringComparison.OrdinalIgnoreCase))
            {
                reserved = false;
                used -= channel.Length + 1;
            }
            var cost = tag.Length + (tag.Contains(' ') ? 2 : 0) + (result.Count > 0 ? 1 : 0);
            if (used + cost > MaxTagsLength) continue;
            result.Add(tag);
            used += cost;
        }
        return result;
    }

    /// <summary>
    /// A qué playlist va el video: la pedida (--playlist) o una deducida del
    /// título y los tags. Teorías es el formato principal del canal → default.
    /// </summary>
    public static string PickPlaylist(VideoPlan plan, string? requested = null)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();
        var text = Fold(plan.Title + " " + string.Join(' ', plan.Tags));
        if (text.Contains("teoria")) return PlaylistTheories;
        if (text.Contains("noticia") || text.Contains("anuncio") || text.Contains("estreno")) return PlaylistNews;
        if (text.Contains("temporada") || text.Contains("recomend")) return PlaylistSeason;
        if (text.Contains("analisis") || text.Contains("explica") || text.Contains("final")) return PlaylistAnalysis;
        return PlaylistTheories;
    }

    /// <summary>¿El título de una playlist del canal es el buscado? Sin tildes ni mayúsculas; "Anime de temporada" matchea "Anime de temporada (recomendaciones)".</summary>
    public static bool PlaylistMatches(string playlistTitle, string wanted) =>
        Fold(playlistTitle).Trim().StartsWith(Fold(wanted).Trim(), StringComparison.Ordinal);

    public static string NormalizePrivacy(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "public" => "public",
            "unlisted" => "unlisted",
            _ => "private",
        };

    private static string Clean(string s) => s.Replace("<", "").Replace(">", "");

    private static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
