using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure;

/// <summary>
/// Pure-HTTP client for katanime.net — used ONLY as a fallback source when jkanime
/// fails for a specific episode (blocked, no mirrors extracted, or every jkanime
/// upload candidate failed). Given a series slug + episode number it resolves that
/// one episode's player embeds to real provider URLs. We never crawl katanime as a
/// whole: if jkanime published an episode it is almost always present on katanime.
///
/// Resolution chain (reverse-engineered from /js/app.js + /js/i.js, verified live):
///   1. GET /capitulo/{slug}-{ep}/   (jkanime slug usually matches; if it 404s we
///      discover katanime's slug via POST /buscar_ajax matching on title).
///   2. Parse &lt;a class="play-video" data-player="{laravel-token}" data-player-name&gt;
///      anchors plus the page-level data-player-uri (https://katanime.net/reproductor).
///   3. GET {player-uri}?url={token}  →  HTML containing  var e = '{"ct","iv","s"}'.
///   4. Decrypt that CryptoJS blob (OpenSSL AES-256-CBC, EVP_BytesToKey/MD5,
///      passphrase "hanabi") → a JSON-encoded real provider embed URL (voe.sx, …).
///
/// The data-player token is a Laravel Crypt payload (AES + HMAC keyed by katanime's
/// APP_KEY — opaque to us); we never decrypt it ourselves, only pass it verbatim to
/// /reproductor, which returns the CryptoJS blob we *can* decrypt.
/// </summary>
public sealed partial class KatanimeHttpClient
{
    private const string BaseUrl = "https://katanime.net";
    private const string Passphrase = "hanabi";

    private readonly HttpClient _http;
    private readonly ILogger<KatanimeHttpClient> _logger;
    private readonly CookieContainer _cookies = new();
    private string? _csrfToken;

    private static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
    ];

    /// <summary>
    /// Player names katanime offers that we skip outright: IP/geo-locked, download-only,
    /// or hosts we have no resolver for. Final filtering is still done downstream by
    /// <c>ResolverRegistry.Supports</c> against the resolved URL hostname.
    /// </summary>
    private static readonly HashSet<string> SkipPlayerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "mega", "desu", "doodstream", "dood", "hexupload", "lulustream", "streamtape",
    };

    public KatanimeHttpClient(ILogger<KatanimeHttpClient> logger)
    {
        _logger = logger;

        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        _http.DefaultRequestHeaders.Add("User-Agent", UserAgents[Random.Shared.Next(UserAgents.Length)]);
        _http.DefaultRequestHeaders.Add("Accept-Language", "es-419,es;q=0.9,en;q=0.5");
    }

    // ── Public API ──────────────────────────────────────────

    /// <summary>
    /// Resolves a single episode's provider embed URLs from katanime as a jkanime fallback.
    /// Tries <c>/capitulo/{jkSlug}-{ep}/</c> directly first; if that has no episode page,
    /// discovers katanime's slug via <c>/buscar_ajax</c> (matched on the supplied title) and
    /// retries. Returns real provider embed URLs (voe.sx, mp4upload, …), filtered against
    /// <see cref="SkipPlayerNames"/>. Never throws — returns an empty list on any failure.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetEpisodeMirrorUrlsAsync(
        string jkSlug, short episodeNumber, string? title, CancellationToken ct)
    {
        try
        {
            // 1. Direct attempt with the jkanime slug (most slugs match across both sites).
            var html = await TryGetEpisodePageAsync(jkSlug, episodeNumber, ct);

            // 2. Fallback: discover katanime's own slug via search, then retry.
            if (html is null)
            {
                var query = !string.IsNullOrWhiteSpace(title) ? title! : jkSlug.Replace('-', ' ');
                var kataSlug = await FindKatanimeSlugAsync(query, jkSlug, ct);
                if (kataSlug is not null &&
                    !string.Equals(kataSlug, jkSlug, StringComparison.OrdinalIgnoreCase))
                {
                    html = await TryGetEpisodePageAsync(kataSlug, episodeNumber, ct);
                }
            }

            if (html is null)
            {
                _logger.LogDebug("Katanime: no episode page found for {Slug} ep{Ep}", jkSlug, episodeNumber);
                return [];
            }

            var urls = await ResolvePlayersAsync(html, ct);
            if (urls.Count > 0)
                _logger.LogInformation("Katanime: resolved {Count} mirror(s) for {Slug} ep{Ep}",
                    urls.Count, jkSlug, episodeNumber);
            return urls;
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Katanime fallback failed for {Slug} ep{Ep}", jkSlug, episodeNumber);
            return [];
        }
    }

    // ── Episode page fetch ──────────────────────────────────

    /// <summary>
    /// GETs <c>/capitulo/{slug}-{ep}/</c>. Returns the HTML only if it is a real episode
    /// page (contains play-video anchors); returns null on 404 / soft-404 / transport error.
    /// </summary>
    private async Task<string?> TryGetEpisodePageAsync(string slug, short ep, CancellationToken ct)
    {
        var url = $"{BaseUrl}/capitulo/{slug}-{ep}/";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Referer", BaseUrl + "/");

            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var html = await resp.Content.ReadAsStringAsync(ct);

            // Capture CSRF token for a possible later search POST.
            var csrf = CsrfTokenRegex().Match(html);
            if (csrf.Success) _csrfToken = csrf.Groups[1].Value;

            // Guard against soft-404 pages that return 200 with no player list.
            return html.Contains("play-video", StringComparison.OrdinalIgnoreCase) ? html : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Katanime: GET {Url} failed", url);
            return null;
        }
    }

    // ── Slug discovery via search ───────────────────────────

    /// <summary>
    /// Resolves katanime's series slug for a title via <c>POST /buscar_ajax</c>.
    /// Prefers an exact slug match with jkanime, else the first "Anime"-category result,
    /// else the first result. Returns null if search is unavailable or empty.
    /// </summary>
    private async Task<string?> FindKatanimeSlugAsync(string query, string jkSlug, CancellationToken ct)
    {
        if (_csrfToken is null)
            await PrimeCsrfAsync(ct);
        if (_csrfToken is null) return null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/buscar_ajax");
            req.Headers.Add("Referer", BaseUrl + "/");
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            req.Headers.Add("X-CSRF-TOKEN", _csrfToken);
            req.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("q", query),
                new KeyValuePair<string, string>("_token", _csrfToken),
            });

            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            KataSearchResponse? parsed;
            try { parsed = JsonSerializer.Deserialize<KataSearchResponse>(json); }
            catch (JsonException) { return null; }

            var list = parsed?.Lista;
            if (list is null || list.Count == 0) return null;

            var exact = list.FirstOrDefault(x =>
                string.Equals(x.Slug, jkSlug, StringComparison.OrdinalIgnoreCase));
            if (exact?.Slug is { Length: > 0 }) return exact.Slug;

            var anime = list.FirstOrDefault(x =>
                x.Slug is { Length: > 0 } &&
                (x.Categoria?.Contains("Anime", StringComparison.OrdinalIgnoreCase) ?? false));
            if (anime?.Slug is { Length: > 0 }) return anime.Slug;

            return list.FirstOrDefault(x => x.Slug is { Length: > 0 })?.Slug;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Katanime: search for '{Query}' failed", query);
            return null;
        }
    }

    /// <summary>GETs the homepage to obtain a CSRF token + session cookie for the search POST.</summary>
    private async Task PrimeCsrfAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/");
            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;

            var html = await resp.Content.ReadAsStringAsync(ct);
            var m = CsrfTokenRegex().Match(html);
            if (m.Success) _csrfToken = m.Groups[1].Value;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* ignore */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Katanime: failed to prime CSRF token");
        }
    }

    // ── Player resolution + decryption ──────────────────────

    /// <summary>
    /// For each non-skipped play-video anchor on the episode page, resolves its Laravel
    /// token through <c>/reproductor</c> and decrypts the returned CryptoJS blob into a
    /// real provider embed URL. Distinct provider URLs are returned.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolvePlayersAsync(string episodeHtml, CancellationToken ct)
    {
        var playerUriMatch = PlayerUriRegex().Match(episodeHtml);
        var playerUri = playerUriMatch.Success ? playerUriMatch.Groups[1].Value : $"{BaseUrl}/reproductor";

        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match anchor in PlayVideoAnchorRegex().Matches(episodeHtml))
        {
            if (ct.IsCancellationRequested) break;

            var tag = anchor.Value;
            var tokenMatch = DataPlayerAttrRegex().Match(tag);
            if (!tokenMatch.Success) continue;

            var name = DataPlayerNameAttrRegex().Match(tag) is { Success: true } nm
                ? nm.Groups[1].Value.Trim()
                : "";
            if (SkipPlayerNames.Contains(name)) continue;

            var resolved = await ResolveTokenAsync(playerUri, tokenMatch.Groups[1].Value, ct);
            if (resolved is not null && resolved.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                urls.Add(resolved);

            // Gentle pacing between resolver round-trips.
            await Task.Delay(Random.Shared.Next(150, 400), ct);
        }

        return [.. urls];
    }

    /// <summary>
    /// GETs <c>{playerUri}?url={token}</c>, extracts the inline <c>var e = '…'</c> CryptoJS
    /// blob, and decrypts it to the real provider URL. Returns null on any failure.
    /// </summary>
    private async Task<string?> ResolveTokenAsync(string playerUri, string token, CancellationToken ct)
    {
        try
        {
            var url = $"{playerUri}?url={Uri.EscapeDataString(token)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Referer", BaseUrl + "/");
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");

            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var html = await resp.Content.ReadAsStringAsync(ct);
            var blob = CryptoBlobRegex().Match(html);
            return blob.Success ? DecryptPlayer(blob.Groups[1].Value) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Katanime: failed to resolve player token");
            return null;
        }
    }

    /// <summary>
    /// Decrypts a CryptoJS <c>{ct,iv,s}</c> blob produced by <c>CryptoJS.AES.encrypt(url, "hanabi")</c>.
    /// Uses the OpenSSL passphrase KDF (EVP_BytesToKey with MD5, 1 iteration) so the JSON <c>iv</c>
    /// is ignored — both key and IV are derived from passphrase + salt. The plaintext is a
    /// JSON-encoded URL string.
    /// </summary>
    private static string? DecryptPlayer(string blobJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(blobJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ct", out var ctEl) || !root.TryGetProperty("s", out var sEl))
                return null;

            var ctB64 = ctEl.GetString();
            var saltHex = sEl.GetString();
            if (string.IsNullOrEmpty(ctB64) || string.IsNullOrEmpty(saltHex)) return null;

            var salt = Convert.FromHexString(saltHex);
            var cipher = Convert.FromBase64String(ctB64);
            var pass = Encoding.UTF8.GetBytes(Passphrase);

            var keyIv = EvpBytesToKey(pass, salt, 32, 16);

            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = keyIv.AsSpan(0, 32).ToArray();
            aes.IV = keyIv.AsSpan(32, 16).ToArray();

            using var dec = aes.CreateDecryptor();
            var plain = dec.TransformFinalBlock(cipher, 0, cipher.Length);
            var text = Encoding.UTF8.GetString(plain);

            // Plaintext is a JSON-encoded string, e.g. "\"https:\\/\\/voe.sx\\/e\\/…\"".
            try { return JsonSerializer.Deserialize<string>(text); }
            catch (JsonException) { return text.Trim('"').Replace("\\/", "/"); }
        }
        catch (Exception)
        {
            // Bad padding / malformed blob / wrong key — treat as unresolvable.
            return null;
        }
    }

    /// <summary>
    /// OpenSSL <c>EVP_BytesToKey</c> (MD5, 1 iteration) — the KDF CryptoJS uses when given a
    /// passphrase. Produces <paramref name="keyLen"/> key bytes followed by <paramref name="ivLen"/>
    /// IV bytes from passphrase + 8-byte salt.
    /// </summary>
    private static byte[] EvpBytesToKey(byte[] passphrase, byte[] salt, int keyLen, int ivLen)
    {
        var target = keyLen + ivLen;
        var derived = new byte[target];
        var produced = 0;
        byte[] block = [];

        while (produced < target)
        {
            var input = new byte[block.Length + passphrase.Length + salt.Length];
            Buffer.BlockCopy(block, 0, input, 0, block.Length);
            Buffer.BlockCopy(passphrase, 0, input, block.Length, passphrase.Length);
            Buffer.BlockCopy(salt, 0, input, block.Length + passphrase.Length, salt.Length);

            block = MD5.HashData(input);
            var take = Math.Min(block.Length, target - produced);
            Buffer.BlockCopy(block, 0, derived, produced, take);
            produced += take;
        }

        return derived;
    }

    // ── Regex helpers ───────────────────────────────────────

    [GeneratedRegex("""meta\s+name="csrf-token"\s+content="([^"]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CsrfTokenRegex();

    [GeneratedRegex("""data-player-uri="([^"]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PlayerUriRegex();

    [GeneratedRegex("""<a\b[^>]*\bclass="[^"]*play-video[^"]*"[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex PlayVideoAnchorRegex();

    [GeneratedRegex("""data-player="([^"]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex DataPlayerAttrRegex();

    [GeneratedRegex("""data-player-name="([^"]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex DataPlayerNameAttrRegex();

    [GeneratedRegex("""var\s+e\s*=\s*'(\{.*?\})'""", RegexOptions.Singleline)]
    private static partial Regex CryptoBlobRegex();

    // ── JSON models ─────────────────────────────────────────

    private sealed record KataSearchResponse(
        [property: JsonPropertyName("lista")] IReadOnlyList<KataSearchItem>? Lista);

    private sealed record KataSearchItem(
        [property: JsonPropertyName("slug")] string? Slug,
        [property: JsonPropertyName("nombre")] string? Nombre,
        [property: JsonPropertyName("categoria")] string? Categoria);
}
