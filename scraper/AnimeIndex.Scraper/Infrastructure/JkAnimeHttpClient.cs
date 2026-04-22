using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure;

/// <summary>
/// Pure HTTP client for JKAnime scraping — replaces Playwright.
/// All directory, detail, episode-list, and mirror data is server-rendered HTML;
/// no JavaScript execution is required.
/// </summary>
public sealed partial class JkAnimeHttpClient
{
    private readonly HttpClient _http;
    private readonly ILogger<JkAnimeHttpClient> _logger;
    private readonly CookieContainer _cookies = new();

    private string? _csrfToken;
    private int _consecutiveFailures;

    private static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:125.0) Gecko/20100101 Firefox/125.0",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
    ];

    /// <summary>Circuit breaker: consecutive failures before pausing.</summary>
    public int CircuitBreakerThreshold { get; set; } = 5;

    /// <summary>How long to pause when circuit breaker trips (ms).</summary>
    public int CircuitBreakerPauseMs { get; set; } = 600_000; // 10 minutes

    public JkAnimeHttpClient(IHttpClientFactory httpClientFactory, ILogger<JkAnimeHttpClient> logger)
    {
        _logger = logger;

        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Add("User-Agent", UserAgents[Random.Shared.Next(UserAgents.Length)]);
        _http.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.Add("Accept-Language", "es-419,es;q=0.9,en;q=0.5");
    }

    // ── Core HTTP helpers ────────────────────────────────────

    /// <summary>GET a page and return its HTML body. Returns null on failure.</summary>
    public async Task<string?> GetPageAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", new Uri(url).GetLeftPart(UriPartial.Authority) + "/");

            var response = await _http.SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                _consecutiveFailures++;
                var backoff = Math.Min(2000 * (1 << _consecutiveFailures), 60_000);
                _logger.LogWarning("Rate limited ({Status}) on {Url} — backing off {Ms}ms",
                    response.StatusCode, url, backoff);
                await Task.Delay(backoff, ct);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _consecutiveFailures++;
                _logger.LogWarning("GET {Url} failed with {Status}", url, response.StatusCode);
                return null;
            }

            _consecutiveFailures = 0;
            var html = await response.Content.ReadAsStringAsync(ct);

            // Extract CSRF token from the page for POST requests
            var csrfMatch = CsrfTokenRegex().Match(html);
            if (csrfMatch.Success)
                _csrfToken = csrfMatch.Groups[1].Value;

            return html;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _consecutiveFailures++;
            _logger.LogWarning(ex, "Failed to GET {Url}", url);
            return null;
        }
    }

    /// <summary>POST to a URL and return the JSON response body.</summary>
    public async Task<string?> PostAsync(string url, string referer, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Referer", referer);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");

            if (_csrfToken is not null)
            {
                request.Headers.Add("X-CSRF-TOKEN", _csrfToken);
                request.Content = new FormUrlEncodedContent(
                    new[] { new KeyValuePair<string, string>("_token", _csrfToken) });
            }

            var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                _consecutiveFailures++;
                _logger.LogWarning("POST {Url} failed with {Status}", url, response.StatusCode);
                return null;
            }

            _consecutiveFailures = 0;
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _consecutiveFailures++;
            _logger.LogWarning(ex, "Failed to POST {Url}", url);
            return null;
        }
    }

    // ── Directory browsing ──────────────────────────────────

    /// <summary>
    /// Fetches a directory page and extracts the embedded <c>var animes = {...}</c> JSON.
    /// Returns deserialized page data or null if page is empty/unreachable.
    /// </summary>
    public async Task<JkDirectoryPage?> GetDirectoryPageAsync(string baseUrl, int page, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/directorio?p={page}";
        var html = await GetPageAsync(url, ct);
        if (html is null) return null;

        var match = VarAnimesRegex().Match(html);
        if (!match.Success)
        {
            _logger.LogDebug("No 'var animes' found on page {Page} — end of directory", page);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JkDirectoryPage>(match.Groups[1].Value);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse directory JSON on page {Page}", page);
            return null;
        }
    }

    // ── Series detail parsing ───────────────────────────────

    /// <summary>
    /// Fetches a series detail page and extracts metadata from the raw HTML.
    /// Primary source: the hidden &lt;div class="card anime_data mov"&gt; card with structured &lt;li&gt; items.
    /// </summary>
    public async Task<SeriesDetailResult?> GetSeriesDetailAsync(string baseUrl, string slug, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/{slug}/";
        var html = await GetPageAsync(url, ct);
        if (html is null) return null;

        try
        {
            // Title — <div class="anime_info">...<h3>Title</h3>
            var title = ExtractFirst(html, AnimeInfoTitleRegex());

            // Synopsis — <p class="scroll">...</p> inside anime_info
            var synopsis = ExtractFirst(html, SynopsisRegex())?.Trim();

            // Cover — CDN convention
            var coverUrl = $"https://cdn.jkdesa.com/assets/images/animes/image/{slug}.jpg";

            // Genres — <a href="/genero/xxx">GenreName</a>
            var genres = GenreRegex().Matches(html)
                .Select(m => m.Groups[1].Value.Trim())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Anime ID — needed for episode AJAX endpoint
            int? animeId = null;
            var idMatch = AnimeIdRegex().Match(html);
            if (idMatch.Success && int.TryParse(idMatch.Groups[1].Value, out var parsedId))
                animeId = parsedId;

            // ── Parse the hidden metadata card ──────────────
            // <div class="card...anime_data mov...">...<ul>...<li>...</li>...</ul>...</div>
            string? status = null;
            string? type = null;
            short? year = null;
            string? studio = null;
            string? season = null;
            string? demographics = null;
            string? language = null;
            short? durationMinutes = null;
            string? airedDate = null;
            string? quality = null;
            short? episodeCount = null;

            var cardMatch = MovCardRegex().Match(html);
            if (cardMatch.Success)
            {
                var card = cardMatch.Value;

                // Type: <li rel="tipo"><span>Tipo:</span> Serie</li>
                var tipoMatch = CardTipoRegex().Match(card);
                if (tipoMatch.Success)
                {
                    var tipoText = tipoMatch.Groups[1].Value.Trim().ToLowerInvariant();
                    type = tipoText switch
                    {
                        var t when t.Contains("serie") || t.Contains("tv") => "tv",
                        var t when t.Contains("película") || t.Contains("pelicula") || t.Contains("movie") => "movie",
                        var t when t.Contains("ova") => "ova",
                        var t when t.Contains("ona") => "ona",
                        var t when t.Contains("especial") || t.Contains("special") => "special",
                        _ => null
                    };
                }

                // Status: <li><span>Estado:</span> <div class="enemision finished">Concluido</div></li>
                var statusMatch = CardStatusClassRegex().Match(card);
                if (statusMatch.Success)
                {
                    var classes = statusMatch.Groups[1].Value.ToLowerInvariant();
                    status = classes switch
                    {
                        var c when c.Contains("finished") || c.Contains("completed") => "completed",
                        var c when c.Contains("currently") => "ongoing",
                        var c when c.Contains("notyet") => "upcoming",
                        _ => null
                    };
                }

                // Studio: <li><span>Studios:</span> <a href="...">Name</a></li>
                studio = ExtractLinkedFieldText(card, "Studios");

                // Season: <li><span>Temporada:</span> <a href="...">Invierno 2024</a></li>
                season = ExtractLinkedFieldText(card, "Temporada");

                // Year from season (e.g., "Invierno 2024" → 2024)
                if (season is not null)
                {
                    var yearMatch = YearRegex().Match(season);
                    if (yearMatch.Success && short.TryParse(yearMatch.Value, out var parsedYear))
                        year = parsedYear;
                }

                // Demographics: <li><span>Demografia:</span> <a href="...">Shoujo</a></li>
                demographics = ExtractLinkedFieldText(card, "Demografia");

                // Language: <li><span>Idiomas:</span>  Japonés  </li>
                language = ExtractPlainFieldText(card, "Idiomas");

                // Episode count: <li><span>Episodios:</span> 12</li>
                var epText = ExtractPlainFieldText(card, "Episodios");
                if (epText is not null && short.TryParse(epText, out var epCount))
                    episodeCount = epCount;

                // Duration: <li><span>Duracion:</span> 24 min.</li>
                var durText = ExtractPlainFieldText(card, "Duraci");
                if (durText is not null)
                {
                    var numMatch = NumberRegex().Match(durText);
                    if (numMatch.Success && short.TryParse(numMatch.Value, out var dur))
                        durationMinutes = dur;
                }

                // Aired date: <li><span> Emitido: </span> Viernes, 05 de Enero de 2024</li>
                airedDate = ExtractPlainFieldText(card, "Emitido");

                // Quality: <li><span>Calidad:</span> 720p</li>
                quality = ExtractPlainFieldText(card, "Calidad");

                // Fallback: year from aired date if not found in season
                if (year is null && airedDate is not null)
                {
                    var yearMatch = YearRegex().Match(airedDate);
                    if (yearMatch.Success && short.TryParse(yearMatch.Value, out var parsedYear))
                        year = parsedYear;
                }
            }

            // ── Alternative titles ──────────────────────────
            string? titleEnglish = null;
            string? titleJapanese = null;
            var altEngMatch = AltTitleRegex("Ingles").Match(html);
            if (altEngMatch.Success)
                titleEnglish = altEngMatch.Groups[1].Value.Trim();
            var altJpnMatch = AltTitleRegex("Japon").Match(html);
            if (altJpnMatch.Success)
                titleJapanese = altJpnMatch.Groups[1].Value.Trim();

            return new SeriesDetailResult(
                Title: title ?? slug,
                Synopsis: synopsis,
                CoverUrl: coverUrl,
                Status: status,
                Type: type ?? "tv",
                Year: year,
                Genres: genres.Count > 0 ? genres : null,
                AnimeId: animeId,
                TitleEnglish: titleEnglish,
                TitleJapanese: titleJapanese,
                Studio: studio,
                Season: season,
                Demographics: demographics,
                Language: language,
                DurationMinutes: durationMinutes,
                AiredDate: airedDate,
                Quality: quality,
                EpisodeCount: episodeCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse detail for {Slug}", slug);
            return null;
        }
    }

    // ── Detail-page extraction helpers ──────────────────────

    /// <summary>
    /// Extracts linked text for a field inside the metadata card.
    /// E.g., for "Studios": &lt;li&gt;&lt;span&gt;Studios:&lt;/span&gt; &lt;a href="..."&gt;Drive&lt;/a&gt;&lt;/li&gt; → "Drive"
    /// </summary>
    private static string? ExtractLinkedFieldText(string cardHtml, string label)
    {
        // Build a regex to find <li> containing the label, then extract <a> text(s)
        var pattern = $@"<li[^>]*>\s*<span[^>]*>\s*{label}\s*:?\s*</span>(.*?)</li>";
        var match = Regex.Match(cardHtml, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success) return null;

        var content = match.Groups[1].Value;
        var anchors = Regex.Matches(content, @">([^<]+)</a>", RegexOptions.IgnoreCase);
        if (anchors.Count > 0)
            return string.Join(", ", anchors.Select(a => a.Groups[1].Value.Trim()));

        // Fallback: plain text
        var text = Regex.Replace(content, @"<[^>]+>", "").Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Extracts plain text for a field inside the metadata card.
    /// E.g., for "Idiomas": &lt;li&gt;&lt;span&gt;Idiomas:&lt;/span&gt;  Japonés  &lt;/li&gt; → "Japonés"
    /// </summary>
    private static string? ExtractPlainFieldText(string cardHtml, string label)
    {
        var pattern = $@"<li[^>]*>\s*<span[^>]*>\s*{label}\s*:?\s*</span>(.*?)</li>";
        var match = Regex.Match(cardHtml, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success) return null;

        var text = Regex.Replace(match.Groups[1].Value, @"<[^>]+>", "").Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Builds a regex for alternative title extraction.
    /// Pattern: &lt;b class="t"&gt;{prefix}...&lt;/b&gt; TEXT (until next &lt;b or end of div)
    /// </summary>
    private static Regex AltTitleRegex(string labelPrefix) =>
        new($@"<b[^>]*class=""t""[^>]*>\s*{labelPrefix}\w*\s*</b>\s*([^<]+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // ── Episode list via AJAX ───────────────────────────────

    /// <summary>
    /// Fetches all episode numbers for a series using the AJAX pagination endpoint.
    /// Requires a prior GET to establish CSRF token and session cookies.
    /// </summary>
    public async Task<IReadOnlyList<JkEpisodeItem>> GetAllEpisodesAsync(
        string baseUrl, int animeId, string slug, CancellationToken ct)
    {
        var referer = $"{baseUrl.TrimEnd('/')}/{slug}/";
        var allEpisodes = new List<JkEpisodeItem>();

        for (var page = 1; page <= 100; page++) // safety cap
        {
            if (ct.IsCancellationRequested) break;

            var url = $"{baseUrl.TrimEnd('/')}/ajax/episodes/{animeId}/{page}";
            var json = await PostAsync(url, referer, ct);
            if (json is null) break;

            JkEpisodesPage? result;
            try
            {
                result = JsonSerializer.Deserialize<JkEpisodesPage>(json);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse episodes JSON for anime {Id} page {Page}",
                    animeId, page);
                break;
            }

            if (result?.Data is null || result.Data.Count == 0) break;

            allEpisodes.AddRange(result.Data);

            if (page >= result.LastPage) break;
        }

        return allEpisodes;
    }

    // ── Episode mirrors ─────────────────────────────────────

    /// <summary>
    /// Fetches an episode page and extracts mirror embed URLs from the
    /// inline <c>var servers = [...]</c> and <c>var video = [...]</c> JavaScript.
    /// Base64-decoded in-process — no browser JS evaluation needed.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetEpisodeMirrorUrlsAsync(string episodeUrl, CancellationToken ct)
    {
        var html = await GetPageAsync(episodeUrl, ct);
        if (html is null) return [];

        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Extract from `var servers = [{ remote: "base64", ... }]`
        var serversMatch = VarServersRegex().Match(html);
        if (serversMatch.Success)
        {
            try
            {
                var servers = JsonSerializer.Deserialize<List<JkServerEntry>>(serversMatch.Groups[1].Value);
                if (servers is not null)
                {
                    foreach (var s in servers)
                    {
                        if (string.IsNullOrWhiteSpace(s.Remote)) continue;
                        if (string.Equals(s.Server, "Mediafire", StringComparison.OrdinalIgnoreCase)) continue;

                        try
                        {
                            var decoded = System.Text.Encoding.UTF8.GetString(
                                Convert.FromBase64String(s.Remote)).Trim();
                            if (decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                urls.Add(decoded);
                        }
                        catch (FormatException) { /* invalid base64 — skip */ }
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Failed to parse servers JSON on {Url}", episodeUrl);
            }
        }

        // 2. Extract OK.ru from `var video = [...]`
        var okruMatch = OkruIdRegex().Match(html);
        if (okruMatch.Success)
            urls.Add($"https://ok.ru/videoembed/{okruMatch.Groups[1].Value}");

        _logger.LogDebug("Episode {Url}: extracted {Count} mirror URLs via HTTP",
            episodeUrl, urls.Count);

        return [.. urls];
    }

    // ── Circuit breaker ─────────────────────────────────────

    public async Task<bool> CheckCircuitBreakerAsync(CancellationToken ct)
    {
        if (_consecutiveFailures < CircuitBreakerThreshold) return false;
        _logger.LogWarning("Circuit breaker tripped ({Failures} failures) — pausing {Ms}ms",
            _consecutiveFailures, CircuitBreakerPauseMs);
        await Task.Delay(CircuitBreakerPauseMs, ct);
        _consecutiveFailures = 0;
        return true;
    }

    // ── Jitter delay ────────────────────────────────────────

    public static async Task JitterDelayAsync(int baseMs, CancellationToken ct)
    {
        var jitter = Random.Shared.Next(-baseMs / 4, baseMs / 4);
        await Task.Delay(Math.Max(200, baseMs + jitter), ct);
    }

    // ── Regex helpers ───────────────────────────────────────

    private static string? ExtractFirst(string html, Regex regex)
    {
        var m = regex.Match(html);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    [GeneratedRegex("""meta\s+name="csrf-token"\s+content="([^"]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CsrfTokenRegex();

    [GeneratedRegex("""var\s+animes\s*=\s*(\{.+?\})\s*;""", RegexOptions.Singleline)]
    private static partial Regex VarAnimesRegex();

    [GeneratedRegex("""var\s+servers\s*=\s*(\[.+?\])\s*;""", RegexOptions.Singleline)]
    private static partial Regex VarServersRegex();

    [GeneratedRegex("""jkokru\.php\?u=(\d+)""")]
    private static partial Regex OkruIdRegex();

    [GeneratedRegex("""<div[^>]*class="anime_info"[^>]*>.*?<h3[^>]*>([^<]+)</h3>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AnimeInfoTitleRegex();

    [GeneratedRegex("""<p[^>]*class="scroll"[^>]*>(.*?)</p>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SynopsisRegex();

    [GeneratedRegex("""/genero/[^"]*">([^<]+)</a>""", RegexOptions.IgnoreCase)]
    private static partial Regex GenreRegex();

    /// <summary>Matches the hidden metadata card: &lt;div class="card...anime_data mov..."&gt;...&lt;/div&gt;&lt;/div&gt;</summary>
    [GeneratedRegex("""<div[^>]*class="[^"]*anime_data\s+mov[^"]*"[^>]*>.*?</ul>\s*</div>\s*</div>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex MovCardRegex();

    /// <summary>Type from card: &lt;li rel="tipo"&gt;&lt;span&gt;Tipo:&lt;/span&gt; Serie&lt;/li&gt;</summary>
    [GeneratedRegex("""<li[^>]*rel="tipo"[^>]*>\s*<span[^>]*>[^<]*</span>\s*([^<]+)</li>""", RegexOptions.IgnoreCase)]
    private static partial Regex CardTipoRegex();

    /// <summary>Status class from card: &lt;div class="enemision finished"&gt;</summary>
    [GeneratedRegex("""<div[^>]*class="[^"]*enemision\s+([^"]+)"[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex CardStatusClassRegex();

    [GeneratedRegex("""\b(19|20)\d{2}\b""")]
    private static partial Regex YearRegex();

    [GeneratedRegex("""\d+""")]
    private static partial Regex NumberRegex();

    [GeneratedRegex("""ajax/(?:personajes|votado|search_episode|episodes)\??\/?(\d+)""")]
    private static partial Regex AnimeIdRegex();
}

// ── JSON models ─────────────────────────────────────────────

public sealed record JkDirectoryPage(
    [property: JsonPropertyName("current_page")] int CurrentPage,
    [property: JsonPropertyName("last_page")] int LastPage,
    [property: JsonPropertyName("data")] IReadOnlyList<JkDirectoryItem> Data);

public sealed record JkDirectoryItem(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("slug")] string? Slug,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("synopsis")] string? Synopsis,
    [property: JsonPropertyName("image")] string? Image,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("status")] string? Status);

public sealed record JkEpisodesPage(
    [property: JsonPropertyName("current_page")] int CurrentPage,
    [property: JsonPropertyName("last_page")] int LastPage,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("data")] IReadOnlyList<JkEpisodeItem> Data);

public sealed record JkEpisodeItem(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("title")] string? Title);

public sealed record JkServerEntry(
    [property: JsonPropertyName("remote")] string? Remote,
    [property: JsonPropertyName("server")] string? Server);

public sealed record SeriesDetailResult(
    string Title,
    string? Synopsis,
    string? CoverUrl,
    string? Status,
    string? Type,
    short? Year,
    IReadOnlyList<string>? Genres,
    int? AnimeId,
    string? TitleEnglish = null,
    string? TitleJapanese = null,
    string? Studio = null,
    string? Season = null,
    string? Demographics = null,
    string? Language = null,
    short? DurationMinutes = null,
    string? AiredDate = null,
    string? Quality = null,
    short? EpisodeCount = null);
