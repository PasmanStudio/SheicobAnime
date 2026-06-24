using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.Importers;

/// <summary>
/// Per-series importer for animeav1.com — pure HTTP, no browser.
///
/// The site is SvelteKit with server-rendered data: the series/episode payload is
/// embedded inline in the HTML as a devalue-serialized object (unquoted keys,
/// <c>void 0</c>, etc.) — NOT strict JSON — so we extract fields with targeted
/// regexes over the relevant object slice instead of <c>System.Text.Json</c>.
///
/// Endpoints:
///   search  → GET /catalogo?search={q}     (cards link to /media/{slug})
///   series  → GET /media/{slug}            (media:{…} + episodes:[{number}])
///   episode → GET /media/{slug}/{n}        (embeds:{SUB:[{server,url}],DUB:[…]})
/// Cover image is built from the media id: https://cdn.animeav1.com/covers/{id}.jpg
/// </summary>
public sealed partial class AnimeAv1Importer(
    IHttpClientFactory httpFactory,
    IConfiguration config,
    ILogger<AnimeAv1Importer> logger) : ISeriesImporter
{
    public string SourceKey => "animeav1";

    private string BaseUrl => (config["AnimeAv1:BaseUrl"] ?? "https://animeav1.com").TrimEnd('/');
    private const string CdnCovers = "https://cdn.animeav1.com/covers";

    // ── Search ───────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceSeriesRef>> SearchAsync(string query, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/catalogo?search={Uri.EscapeDataString(query)}";
        var html = await GetAsync(url, ct);
        if (html is null) return [];

        // Result cards link to /media/{slug}. Preserve first-seen order (= relevance) and dedup.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<SourceSeriesRef>();
        foreach (Match m in MediaSlugRegex().Matches(html))
        {
            var slug = m.Groups[1].Value;
            if (seen.Add(slug))
                refs.Add(new SourceSeriesRef(slug, null));
            if (refs.Count >= 15) break;
        }
        return refs;
    }

    // ── Series detail ────────────────────────────────────────

    public async Task<SourceSeries?> FetchSeriesAsync(string slug, CancellationToken ct = default)
    {
        var html = await GetAsync($"{BaseUrl}/media/{slug}", ct);
        if (html is null) return null;

        // Isolate the media object so string fields (title/synopsis) can't be
        // confused with anything later on the page.
        var media = SliceBalanced(html, "media:{");
        if (media is null)
        {
            logger.LogWarning("animeav1: no media object on /media/{Slug}", slug);
            return null;
        }

        var title = Unescape(MatchString(media, "title")) ?? slug;
        var synopsis = Unescape(MatchString(media, "synopsis"));

        var id = MatchInt(media, "id");
        var coverUrl = id is not null ? $"{CdnCovers}/{id}.jpg" : null;

        var startDate = MatchDate(media, "startDate");
        var endDate = MatchDate(media, "endDate");
        short? year = startDate is { Length: >= 4 } && short.TryParse(startDate[..4], out var y) ? y : null;

        var status = DeriveStatus(startDate, endDate);
        var type = MapType(MatchCategorySlug(media));
        var genres = ExtractGenres(media);
        var episodeNumbers = ExtractEpisodeNumbers(media);

        logger.LogInformation(
            "animeav1: {Slug} → \"{Title}\" status={Status} type={Type} eps={Eps}",
            slug, title, status, type, episodeNumbers.Count);

        return new SourceSeries(
            Slug: slug,
            Title: title,
            Synopsis: synopsis,
            CoverUrl: coverUrl,
            Status: status,
            Type: type,
            Year: year,
            Genres: genres.Count > 0 ? genres : null,
            EpisodeNumbers: episodeNumbers);
    }

    // ── Episode embeds (SUB only) ────────────────────────────

    public async Task<IReadOnlyList<SourceEmbed>> FetchEpisodeEmbedsAsync(
        string slug, short episodeNumber, CancellationToken ct = default)
    {
        var html = await GetAsync($"{BaseUrl}/media/{slug}/{episodeNumber}", ct);
        if (html is null) return [];

        var embeds = SliceBalanced(html, "embeds:{");
        if (embeds is null) return [];

        // Only the SUB group. Its array elements are flat {server,url} objects with
        // no nested arrays, so the first ']' after "SUB:[" closes the group.
        var subStart = embeds.IndexOf("SUB:[", StringComparison.Ordinal);
        if (subStart < 0) return [];
        var arrStart = subStart + "SUB:[".Length;
        var arrEnd = embeds.IndexOf(']', arrStart);
        if (arrEnd < 0) return [];
        var subBlock = embeds[arrStart..arrEnd];

        var list = new List<SourceEmbed>();
        foreach (Match m in EmbedPairRegex().Matches(subBlock))
            list.Add(new SourceEmbed(m.Groups[1].Value, m.Groups[2].Value));
        return list;
    }

    // ── Parsing helpers ──────────────────────────────────────

    /// <summary>
    /// Returns the substring starting right after <paramref name="opener"/> (which must
    /// end in '{') up to its matching closing brace, respecting double-quoted strings and
    /// backslash escapes. Null if the opener or a balanced close isn't found.
    /// </summary>
    private static string? SliceBalanced(string html, string opener)
    {
        var start = html.IndexOf(opener, StringComparison.Ordinal);
        if (start < 0) return null;
        var i = start + opener.Length;      // first char inside the object
        var depth = 1;
        var inStr = false;
        for (; i < html.Length; i++)
        {
            var c = html[i];
            if (inStr)
            {
                if (c == '\\') { i++; continue; }   // skip escaped char
                if (c == '"') inStr = false;
            }
            else
            {
                if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                    return html[(start + opener.Length)..i];
            }
        }
        return null;
    }

    private static string? MatchString(string s, string key)
    {
        var m = Regex.Match(s, key + ":\"((?:\\\\.|[^\"\\\\])*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static int? MatchInt(string s, string key)
    {
        var m = Regex.Match(s, "\\b" + key + ":(\\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static string? MatchDate(string s, string key)
    {
        var m = Regex.Match(s, key + ":\"(\\d{4}-\\d{2}-\\d{2})");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? MatchCategorySlug(string s)
    {
        var m = Regex.Match(s, "category:\\{[^}]*?slug:\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static List<string> ExtractGenres(string media)
    {
        var genres = new List<string>();
        var m = Regex.Match(media, "genres:\\[(.*?)\\]");
        if (!m.Success) return genres;
        foreach (Match g in Regex.Matches(m.Groups[1].Value, "name:\"([^\"]+)\""))
            genres.Add(g.Groups[1].Value);
        return genres;
    }

    private static List<short> ExtractEpisodeNumbers(string media)
    {
        var nums = new List<short>();
        var m = Regex.Match(media, "episodes:\\[(.*?)\\]");
        if (!m.Success) return nums;
        foreach (Match e in Regex.Matches(m.Groups[1].Value, "number:(\\d+)"))
            if (short.TryParse(e.Groups[1].Value, out var n) && n > 0)
                nums.Add(n);
        return nums.Distinct().OrderBy(n => n).ToList();
    }

    /// <summary>completed if it has ended, ongoing if it started, otherwise upcoming.</summary>
    private static string DeriveStatus(string? startDate, string? endDate)
    {
        var today = DateTime.UtcNow.Date;
        if (endDate is not null && DateTime.TryParse(endDate, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var end) && end.Date <= today)
            return "completed";
        if (startDate is not null && DateTime.TryParse(startDate, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start) && start.Date <= today)
            return "ongoing";
        return "upcoming";
    }

    private static string MapType(string? categorySlug) => (categorySlug ?? "").ToLowerInvariant() switch
    {
        var s when s.Contains("movie") || s.Contains("pelicula") => "movie",
        var s when s.Contains("ova") => "ova",
        var s when s.Contains("ona") => "ona",
        var s when s.Contains("especial") || s.Contains("special") => "special",
        _ => "tv",
    };

    /// <summary>Minimal JSON-style unescape for the devalue string values we read.</summary>
    private static string? Unescape(string? s) => s?
        .Replace("\\n", "\n")
        .Replace("\\\"", "\"")
        .Replace("\\/", "/")
        .Replace("\\\\", "\\")
        .Trim();

    // ── HTTP ─────────────────────────────────────────────────

    private async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient("animeav1");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("animeav1: GET {Url} → HTTP {Status}", url, (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "animeav1: GET {Url} failed", url);
            return null;
        }
    }

    [GeneratedRegex(@"/media/([a-z0-9][a-z0-9-]*)")]
    private static partial Regex MediaSlugRegex();

    [GeneratedRegex("server:\"([^\"]+)\",url:\"([^\"]+)\"")]
    private static partial Regex EmbedPairRegex();
}
