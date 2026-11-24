using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>Imagen candidata para una toma, con de dónde salió.</summary>
public sealed record VisualCandidate(string Url, string Title, int Width, int Height, string Source);

/// <summary>
/// Busca imágenes que muestren LO QUE DICE cada toma, para cualquier tema, sin
/// nombres escritos a mano:
///   • la wiki de Fandom de la serie, descubierta desde su nombre
///     ("Cyberpunk: Edgerunners" → cyberpunk.fandom.com; "Frieren" → frieren.fandom.com).
///     Sus archivos son capturas de episodios con nombres descriptivos
///     ("Aura activates the Scales of Obedience EP10").
///   • la imagen oficial de cada personaje de AniList, si la toma lo nombra.
/// Se descarta lo que no es una escena (figuras, dioramas, logos, páginas del manga).
/// </summary>
public sealed partial class VisualSearchService(IHttpClientFactory httpFactory, ILogger<VisualSearchService> logger)
{
    private string? _wiki;
    private bool _wikiResolved;
    private List<(string Name, string Image)> _characters = [];
    private readonly Dictionary<string, List<VisualCandidate>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string? WikiBase => _wiki;

    /// <summary>Descubre la wiki y trae los personajes. Una vez por video.</summary>
    public async Task InitAsync(IEnumerable<string> titles, string? anime, CancellationToken ct)
    {
        if (!_wikiResolved)
        {
            _wikiResolved = true;
            foreach (var slug in WikiSlugCandidates(titles))
            {
                var stats = await TryWikiStatsAsync(slug, ct);
                if (stats is { Articles: >= 300 })
                {
                    _wiki = $"https://{slug}.fandom.com";
                    logger.LogInformation("Wiki de la serie: {Wiki} ({Articles} artículos, {Images} imágenes)",
                        _wiki, stats.Value.Articles, stats.Value.Images);
                    break;
                }
            }
            if (_wiki is null) logger.LogInformation("Sin wiki de Fandom para {Titles}", string.Join(" / ", titles));
        }
        if (anime is not null && _characters.Count == 0)
            _characters = await TryCharactersAsync(anime, ct);
    }

    /// <summary>
    /// Direcciones posibles de la wiki a partir de los títulos: todo junto, la
    /// primera palabra, la última, con guiones. Público para tests.
    /// </summary>
    public static List<string> WikiSlugCandidates(IEnumerable<string> titles)
    {
        var slugs = new List<string>();
        foreach (var title in titles.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            var words = NonAlnum().Split(title.ToLowerInvariant()).Where(w => w.Length > 0 && w != "the" && w != "no").ToList();
            if (words.Count == 0) continue;
            slugs.Add(string.Concat(words));
            slugs.Add(words[0]);
            if (words.Count > 1) slugs.Add(string.Concat(words.Take(2)));
            slugs.Add(words[^1]);
            slugs.Add(string.Join('-', words));
        }
        return slugs.Where(s => s.Length >= 3 && !s.All(char.IsDigit)).Distinct().ToList();
    }

    /// <summary>
    /// Imágenes para una búsqueda: retratos de los personajes nombrados primero
    /// si la búsqueda es solo un personaje, después capturas de la wiki
    /// (episodios antes que el resto). Resultados en caché por búsqueda.
    /// </summary>
    public async Task<List<VisualCandidate>> SearchAsync(string query, CancellationToken ct)
    {
        if (_cache.TryGetValue(query, out var cached)) return cached;
        var found = new List<VisualCandidate>();

        if (_wiki is not null)
        {
            var url = $"{_wiki}/api.php?action=query&generator=search&gsrsearch={Uri.EscapeDataString(query)}" +
                      "&gsrnamespace=6&gsrlimit=20&prop=imageinfo&iiprop=url|size|mime&format=json&formatversion=2";
            try
            {
                using var doc = JsonDocument.Parse(await httpFactory.CreateClient("youtube-research").GetStringAsync(url, ct));
                if (doc.RootElement.TryGetProperty("query", out var q))
                    foreach (var page in q.GetProperty("pages").EnumerateArray())
                    {
                        if (!page.TryGetProperty("imageinfo", out var ii)) continue;
                        var info = ii[0];
                        var title = page.GetProperty("title").GetString() ?? "";
                        var mime = info.TryGetProperty("mime", out var m) ? m.GetString() ?? "" : "";
                        var w = info.GetProperty("width").GetInt32();
                        var h = info.GetProperty("height").GetInt32();
                        if (IsUsableScene(title, mime, w, h))
                            found.Add(new VisualCandidate(info.GetProperty("url").GetString()!, title, w, h, "wiki"));
                    }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException)
            {
                logger.LogWarning("Wiki: búsqueda '{Query}' falló: {Msg}", query, ex.Message);
            }
            // Las capturas de episodios primero (EP7, Episode 3…), después lo más grande.
            found = found.OrderByDescending(c => EpisodeShot().IsMatch(c.Title)).ThenByDescending(c => c.Width).ToList();
        }

        // Personajes nombrados en la búsqueda: su retrato oficial. Si la búsqueda
        // es solo el nombre, va primero; si describe una escena, al final.
        var named = _characters.Where(c => NameMentioned(query, c.Name)).ToList();
        var portraits = named.Select(c => new VisualCandidate(c.Image, c.Name, 460, 650, "anilist")).ToList();
        var onlyName = named.Count > 0 && query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 2;
        found = onlyName ? [.. portraits, .. found] : [.. found, .. portraits];

        _cache[query] = found;
        return found;
    }

    /// <summary>
    /// Texto de los artículos de la wiki sobre el tema, como fuente para el guion.
    /// </summary>
    public async Task<List<ResearchSource>> WikiArticlesAsync(string query, int max, CancellationToken ct)
    {
        var sources = new List<ResearchSource>();
        if (_wiki is null) return sources;
        try
        {
            var http = httpFactory.CreateClient("youtube-research");
            using var search = JsonDocument.Parse(await http.GetStringAsync(
                $"{_wiki}/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(query)}&srlimit={max}&format=json&formatversion=2", ct));
            foreach (var hit in search.RootElement.GetProperty("query").GetProperty("search").EnumerateArray())
            {
                var title = hit.GetProperty("title").GetString()!;
                using var parse = JsonDocument.Parse(await http.GetStringAsync(
                    $"{_wiki}/api.php?action=parse&page={Uri.EscapeDataString(title)}&prop=text&format=json&formatversion=2", ct));
                var html = parse.RootElement.GetProperty("parse").GetProperty("text").GetString() ?? "";
                var text = VideoResearchService.StripHtml(html);
                if (text.Length > 300)
                    sources.Add(new ResearchSource($"Wiki: {title}", $"{_wiki}/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}",
                        text.Length > 10_000 ? text[..10_000] : text));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException)
        {
            logger.LogWarning("Wiki: artículos de '{Query}' fallaron: {Msg}", query, ex.Message);
        }
        return sources;
    }

    /// <summary>
    /// ¿Sirve como toma? Imagen grande (≥ 960 px de ancho, horizontal) y que no
    /// sea merchandising, logo, ícono ni página de manga. Público para tests.
    /// </summary>
    public static bool IsUsableScene(string title, string mime, int width, int height) =>
        mime is "image/png" or "image/jpeg" or "image/webp"
        && width >= 960 && width >= height
        && !NotAScene().IsMatch(title);

    private static bool NameMentioned(string query, string name)
    {
        var first = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
        return first.Length >= 3 && Regex.IsMatch(query, $@"\b{Regex.Escape(first)}\b", RegexOptions.IgnoreCase);
    }

    private async Task<(int Articles, int Images)?> TryWikiStatsAsync(string slug, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await httpFactory.CreateClient("youtube-research").GetStringAsync(
                $"https://{slug}.fandom.com/api.php?action=query&meta=siteinfo&siprop=statistics&format=json&formatversion=2", ct));
            var st = doc.RootElement.GetProperty("query").GetProperty("statistics");
            return (st.GetProperty("articles").GetInt32(), st.GetProperty("images").GetInt32());
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException)
        {
            return null;
        }
    }

    private async Task<List<(string Name, string Image)>> TryCharactersAsync(string anime, CancellationToken ct)
    {
        const string query = """
            query ($search: String) {
              Media(search: $search, type: ANIME) {
                characters(perPage: 40, sort: [ROLE, FAVOURITES_DESC]) { nodes { name { full } image { large } } }
              }
            }
            """;
        try
        {
            var payload = JsonSerializer.Serialize(new { query, variables = new { search = anime } });
            using var resp = await httpFactory.CreateClient("youtube-research").PostAsync("https://graphql.anilist.co",
                new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var list = doc.RootElement.GetProperty("data").GetProperty("Media").GetProperty("characters").GetProperty("nodes")
                .EnumerateArray()
                .Select(n => (n.GetProperty("name").GetProperty("full").GetString() ?? "",
                              n.GetProperty("image").GetProperty("large").GetString() ?? ""))
                .Where(c => c.Item1.Length > 0 && c.Item2.Length > 0 && !c.Item2.Contains("default.jpg", StringComparison.Ordinal))
                .ToList();
            logger.LogInformation("Personajes de AniList: {N}", list.Count);
            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning("AniList: personajes de '{Anime}' fallaron: {Msg}", anime, ex.Message);
            return [];
        }
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlnum();

    [GeneratedRegex(@"\b(EP|E|Ep|Episode|Episodio)\s*\d+|\bS\d+E\d+|\bEP\d+", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeShot();

    [GeneratedRegex(@"statue|figure|figurine|diorama|scale|plush|nendoroid|cosplay|merch|poster|logo|icon|banner|card|sticker|keychain|manga|chapter|\bCH\d+|volume|\bvol\b|cover|signature|map|sprite|render|model|wallpaper|promo\s*art|key\s*visual|blu-ray|dvd|box|trading", RegexOptions.IgnoreCase)]
    private static partial Regex NotAScene();
}
