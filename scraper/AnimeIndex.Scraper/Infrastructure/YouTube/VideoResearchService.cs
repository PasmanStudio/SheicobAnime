using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>Material reunido sobre el tema, antes de pasar por la IA.</summary>
public sealed record VideoResearch(
    string Topic,
    List<ResearchSource> Sources,
    string? CoverImageUrl,
    string? BannerImageUrl,
    string? TrailerUrl = null);

public sealed record ResearchSource(string Name, string Url, string Text);

/// <summary>
/// Investigación SIN IA: el backend junta las fuentes y Gemini solo las lee.
/// Así la IA no hace de crawler (el grounding de Gemini da 429 en el free tier)
/// y cada dato del guion sale de un texto que tenemos guardado.
///
/// Fuentes: AniList (ficha + arte oficial), Wikipedia en español e inglés, y
/// las URLs que se pasen a mano (la nota que originó el reel, por ejemplo).
/// </summary>
public sealed partial class VideoResearchService(
    IHttpClientFactory httpFactory,
    ILogger<VideoResearchService> logger)
{
    private const int MaxCharsPerSource = 12_000;

    public async Task<VideoResearch> GatherAsync(
        string topic, string? anime, IReadOnlyList<string> urls, CancellationToken ct)
    {
        var sources = new List<ResearchSource>();
        string? cover = null, banner = null, trailer = null;
        var searchTerm = anime ?? topic;

        if (anime is not null)
        {
            var aniList = await TryAniListAsync(anime, ct);
            if (aniList is not null)
            {
                sources.Add(aniList.Value.Source);
                cover = aniList.Value.Cover;
                banner = aniList.Value.Banner;
                trailer = aniList.Value.Trailer;
            }
        }

        foreach (var lang in new[] { "es", "en" })
        {
            var wiki = await TryWikipediaAsync(lang, searchTerm, ct);
            if (wiki is not null) sources.Add(wiki);
        }

        foreach (var url in urls)
        {
            var page = await TryFetchPageAsync(url, ct);
            if (page is not null) sources.Add(page);
        }

        logger.LogInformation("Investigación: {Count} fuentes ({Names}), arte oficial={HasArt}, tráiler={Trailer}",
            sources.Count, string.Join(", ", sources.Select(s => s.Name)), cover is not null, trailer ?? "no");
        return new VideoResearch(topic, sources, cover, banner, trailer);
    }

    private async Task<(ResearchSource Source, string? Cover, string? Banner, string? Trailer)?> TryAniListAsync(
        string anime, CancellationToken ct)
    {
        const string query = """
            query ($search: String) {
              Media(search: $search, type: ANIME) {
                siteUrl
                title { romaji english native }
                description(asHtml: false)
                startDate { year }
                episodes format status genres averageScore
                studios(isMain: true) { nodes { name } }
                bannerImage
                coverImage { extraLarge }
                trailer { id site }
              }
            }
            """;
        try
        {
            var http = httpFactory.CreateClient("youtube-research");
            var payload = JsonSerializer.Serialize(new { query, variables = new { search = anime } });
            using var resp = await http.PostAsync("https://graphql.anilist.co",
                new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("AniList {Code} para '{Anime}'", (int)resp.StatusCode, anime);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var m = doc.RootElement.GetProperty("data").GetProperty("Media");
            if (m.ValueKind == JsonValueKind.Null) return null;

            static string? S(JsonElement e, string p) =>
                e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var t = m.GetProperty("title");
            var studios = m.GetProperty("studios").GetProperty("nodes").EnumerateArray()
                .Select(n => S(n, "name")).Where(n => n is not null);
            var genres = m.GetProperty("genres").EnumerateArray().Select(g => g.GetString());
            var text = new StringBuilder()
                .AppendLine($"Título: {S(t, "romaji")} / {S(t, "english")} / {S(t, "native")}")
                .AppendLine($"Año: {m.GetProperty("startDate").GetProperty("year")}  Formato: {S(m, "format")}  Estado: {S(m, "status")}")
                .AppendLine($"Episodios: {m.GetProperty("episodes")}  Estudio: {string.Join(", ", studios)}")
                .AppendLine($"Géneros: {string.Join(", ", genres)}  Puntaje AniList: {m.GetProperty("averageScore")}")
                .AppendLine(StripHtml(S(m, "description") ?? ""))
                .ToString();

            var cover = m.TryGetProperty("coverImage", out var c) ? S(c, "extraLarge") : null;
            // Tráiler oficial: de ahí salen los clips y fotogramas reales del video.
            string? trailer = null;
            if (m.TryGetProperty("trailer", out var tr) && tr.ValueKind == JsonValueKind.Object
                && S(tr, "site") == "youtube" && S(tr, "id") is { } trailerId)
                trailer = $"https://www.youtube.com/watch?v={trailerId}";
            return (new ResearchSource("AniList", S(m, "siteUrl") ?? "https://anilist.co", text),
                cover, S(m, "bannerImage"), trailer);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            logger.LogWarning(ex, "AniList falló para '{Anime}'", anime);
            return null;
        }
    }

    private async Task<ResearchSource?> TryWikipediaAsync(string lang, string term, CancellationToken ct)
    {
        var url = $"https://{lang}.wikipedia.org/w/api.php?action=query&generator=search" +
                  $"&gsrsearch={Uri.EscapeDataString(term)}&gsrlimit=1&prop=extracts|info&inprop=url" +
                  "&explaintext=1&format=json&formatversion=2";
        try
        {
            var http = httpFactory.CreateClient("youtube-research");
            var body = await http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("query", out var q)) return null;
            var page = q.GetProperty("pages")[0];
            var extract = page.TryGetProperty("extract", out var e) ? e.GetString() ?? "" : "";
            if (extract.Length < 200) return null;
            return new ResearchSource($"Wikipedia ({lang})",
                page.GetProperty("fullurl").GetString() ?? "", Truncate(extract, MaxCharsPerSource));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Wikipedia {Lang} falló para '{Term}'", lang, term);
            return null;
        }
    }

    private async Task<ResearchSource?> TryFetchPageAsync(string url, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient("youtube-research");
            var html = await http.GetStringAsync(url, ct);
            var text = StripHtml(html);
            return text.Length < 200 ? null
                : new ResearchSource(new Uri(url).Host, url, Truncate(text, MaxCharsPerSource));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            logger.LogWarning(ex, "No se pudo leer {Url}", url);
            return null;
        }
    }

    /// <summary>HTML → texto plano: fuera script/style/nav, fuera tags, espacios colapsados.</summary>
    public static string StripHtml(string html)
    {
        var t = NoiseBlocks().Replace(html, " ");
        t = BreakTags().Replace(t, "\n");
        t = Tags().Replace(t, " ");
        t = WebUtility.HtmlDecode(t);
        t = Spaces().Replace(t, " ");
        t = BlankLines().Replace(t, "\n");
        return t.Trim();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    [GeneratedRegex(@"<(script|style|nav|header|footer|noscript|svg)\b[^>]*>.*?</\1>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex NoiseBlocks();

    [GeneratedRegex(@"<(br|/p|/div|/h\d|/li)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s*\n\s*")]
    private static partial Regex BlankLines();
}
