using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure;

/// <summary>
/// HTTP client for the SeekStreaming API v1.
/// Auth: header "api-token: KEY"
///
/// Upload flow (tus direct upload):
///   1. GET /api/v1/video/upload          → { tusUrl, accessToken }
///   2. HEAD sourceUrl                    → Content-Length (file size)
///   3. POST tusUrl                       → 201 + Location header (upload slot)
///   4. PATCH Location in 50 MB chunks   → 204 per chunk (stream download → upload)
///   5. Poll GET /api/v1/video/manage    → find video by name, wait for status=="Active"
///   6. Embed URL: https://sheicobanime.seekplayer.me/#{videoId}
///
/// Named HttpClients required:
///   "seekstreaming"   — API calls (base URL + api-token header)
///   "seek-download"   — Downloading source MP4 (User-Agent set, 60-min timeout)
///   "seek-tus"        — tus PATCH uploads (60-min timeout)
///
/// Config key: "SeekStreaming:ApiKey"  (env var: SEEKSTREAMING__APIKEY)
/// </summary>
public sealed class SeekStreamingClient
{
    private readonly HttpClient _http;
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _baseUrl;
    private readonly int _transcodeFloorMinutes;
    private readonly int _transcodeCapMinutes;
    private readonly ILogger<SeekStreamingClient> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public SeekStreamingClient(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<SeekStreamingClient> logger)
    {
        _logger = logger;
        _httpFactory = httpFactory;

        var apiKey = config["SeekStreaming:ApiKey"]
            ?? throw new InvalidOperationException(
                "SeekStreaming:ApiKey is not configured. Set env var SEEKSTREAMING__APIKEY.");

        _baseUrl = config["SeekStreaming:BaseUrl"]?.TrimEnd('/') ?? "https://seekstreaming.com";

        // Los transcodes exitosos terminan en ~1-3 min; un archivo que sigue
        // "Pending" pasado el piso casi nunca se recupera y solo estanca la cola
        // paralela de Phase B. Piso 8 / tope 12 min (configurable) en vez de 15-20.
        _transcodeFloorMinutes = config.GetValue("SeekStreaming:TranscodeTimeoutMinutes", 8);
        _transcodeCapMinutes = config.GetValue("SeekStreaming:TranscodeTimeoutCapMinutes", 12);

        _http = httpFactory.CreateClient("seekstreaming");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("api-token", apiKey);
    }

    /// <summary>
    /// Downloads <paramref name="directVideoUrl"/> and uploads it to SeekStreaming via tus protocol.
    /// Our scraper fetches the bytes itself, bypassing SeekStreaming's remote-URL downloader
    /// (which fails on port 183 and IP-bound HLS tokens).
    /// Returns the embed URL on success, null on failure.
    /// </summary>
    public async Task<string?> UploadFromUrlAsync(
        string directVideoUrl,
        string? referer = null,
        int pollTimeoutMinutes = 0, // 0 = usar el piso configurado (SeekStreaming:TranscodeTimeoutMinutes)
        Guid? episodeId = null,
        string? provider = null,
        CancellationToken ct = default)
    {
        // ─── Step 1: get tus credentials ──────────────────────────
        TusCredentials? creds;
        try
        {
            using var credResp = await _http.GetAsync($"{_baseUrl}/api/v1/video/upload", ct);
            credResp.EnsureSuccessStatusCode();
            var credJson = await credResp.Content.ReadAsStringAsync(ct);
            creds = JsonSerializer.Deserialize<TusCredentials>(credJson, JsonOpts);
        }
        catch (Exception ex) when (ex is not TaskCanceledException { CancellationToken.IsCancellationRequested: true })
        {
            _logger.LogWarning(ex, "SeekStreaming: failed to get tus credentials (ep={EpisodeId} via={Provider})",
                episodeId, provider);
            return null;
        }

        if (creds is null || string.IsNullOrEmpty(creds.TusUrl) || string.IsNullOrEmpty(creds.AccessToken))
        {
            _logger.LogWarning("SeekStreaming: invalid tus credentials response (ep={EpisodeId} via={Provider})",
                episodeId, provider);
            return null;
        }

        // ─── Step 2: resolve file size (HEAD → Range GET → plain GET) ──
        // Some CDNs (mp4upload) block HEAD from cloud IPs → fall back to a
        // 1-byte GET Range request to read Content-Range: bytes 0-0/TOTAL.
        // mixdrop (mxcontent.net) bloquea HEAD y Range pero responde el GET
        // normal con Content-Length — y si ni eso, descargamos a disco para
        // medir (tus exige Upload-Length por adelantado).
        long fileSize;
        string? tempFilePath = null;
        using (var dlClient = _httpFactory.CreateClient("seek-download"))
        {
            fileSize = await GetFileSizeAsync(dlClient, directVideoUrl, referer, ct);
            if (fileSize <= 0)
            {
                _logger.LogDebug(
                    "SeekStreaming: size unknown via headers — spooling to temp file (ep={EpisodeId} via={Provider})",
                    episodeId, provider);
                tempFilePath = await SpoolToTempFileAsync(dlClient, directVideoUrl, referer, episodeId, provider, ct);
                if (tempFilePath is null)
                {
                    // Per-attempt: el caller prueba el siguiente candidato/katanime.
                    _logger.LogDebug(
                        "SeekStreaming: could not determine file size (ep={EpisodeId} via={Provider}) for {Url}",
                        episodeId, provider, directVideoUrl);
                    return null;
                }
                fileSize = new FileInfo(tempFilePath).Length;
            }
        }

        // ─── Step 3: create tus upload slot ───────────────────────
        var filename = $"sa_{Guid.NewGuid().ToString("N")[..8]}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.mp4";
        string uploadUrl;
        try
        {
            var metadata = BuildTusMetadata(creds.AccessToken, filename, "video/mp4");
            using var tusClient = _httpFactory.CreateClient("seek-tus");
            using var createReq = new HttpRequestMessage(HttpMethod.Post, creds.TusUrl);
            createReq.Headers.TryAddWithoutValidation("Tus-Resumable", "1.0.0");
            createReq.Headers.TryAddWithoutValidation("Upload-Length", fileSize.ToString());
            createReq.Headers.TryAddWithoutValidation("Upload-Metadata", metadata);
            createReq.Content = new ByteArrayContent([]);
            createReq.Content.Headers.ContentLength = 0;

            using var createResp = await tusClient.SendAsync(createReq, ct);
            createResp.EnsureSuccessStatusCode();
            uploadUrl = createResp.Headers.Location?.ToString()
                ?? throw new InvalidOperationException("Tus create returned no Location header");
        }
        catch (Exception ex) when (ex is not TaskCanceledException { CancellationToken.IsCancellationRequested: true })
        {
            _logger.LogWarning(ex,
                "SeekStreaming: failed to create tus upload slot (ep={EpisodeId} via={Provider}) for {Url}",
                episodeId, provider, directVideoUrl);
            if (tempFilePath is not null)
                try { File.Delete(tempFilePath); } catch { /* best-effort cleanup */ }
            return null;
        }

        _logger.LogInformation(
            "SeekStreaming tus upload started: ep={EpisodeId} via={Provider} filename={Filename} size={Size} url={Url}",
            episodeId, provider, filename, fileSize, directVideoUrl);

        // ─── Step 4: stream download → tus PATCH in 50 MB chunks ──
        // Chunks are retried up to 3 times on transient SeekStreaming errors (502/503/429/504).
        // The buffer already holds the downloaded bytes so retrying only resends the PATCH —
        // no re-download needed. offset is only incremented on a successful PATCH.
        const long ChunkSize = 52_428_800L; // 50 MB — SeekStreaming recommended
        const int MaxChunkRetries = 3;
        try
        {
            using var dlClient = _httpFactory.CreateClient("seek-download");
            using var tusClient = _httpFactory.CreateClient("seek-tus");

            // Fuente: el temp file (si tuvimos que spoolear para medir) o el stream de red
            HttpResponseMessage? downloadResp = null;
            Stream downloadStream;
            if (tempFilePath is not null)
            {
                downloadStream = File.OpenRead(tempFilePath);
            }
            else
            {
                using var downloadReq = new HttpRequestMessage(HttpMethod.Get, directVideoUrl);
                downloadReq.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
                if (!string.IsNullOrEmpty(referer))
                    downloadReq.Headers.TryAddWithoutValidation("Referer", referer);
                downloadResp = await dlClient.SendAsync(
                    downloadReq, HttpCompletionOption.ResponseHeadersRead, ct);
                downloadResp.EnsureSuccessStatusCode();
                downloadStream = await downloadResp.Content.ReadAsStreamAsync(ct);
            }
            using var _downloadResp = downloadResp;
            await using var _downloadStream = downloadStream;
            var buffer = new byte[ChunkSize];
            long offset = 0;

            while (offset < fileSize && !ct.IsCancellationRequested)
            {
                var toRead = (int)Math.Min(ChunkSize, fileSize - offset);
                var bytesRead = 0;
                while (bytesRead < toRead)
                {
                    var n = await downloadStream.ReadAsync(buffer.AsMemory(bytesRead, toRead - bytesRead), ct);
                    if (n == 0) break; // unexpected EOF
                    bytesRead += n;
                }
                if (bytesRead == 0) break;

                // Retry loop — buffer already holds the chunk bytes so we can recreate
                // the PATCH request on each attempt without re-downloading from the source.
                System.Net.HttpStatusCode? lastFailStatus = null;
                long? serverOffsetOverride = null; // set when server confirms it has the chunk

                for (var attempt = 0; attempt < MaxChunkRetries; attempt++)
                {
                    if (attempt > 0)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)); // 4s, 8s
                        _logger.LogWarning(
                            "SeekStreaming tus PATCH retry {Attempt}/{Max} after {Status} (ep={EpisodeId} via={Provider} offset={Offset}) — waiting {Delay}s",
                            attempt, MaxChunkRetries, lastFailStatus, episodeId, provider, offset, delay.TotalSeconds);

                        // tus protocol: after any transient error, HEAD the upload to get the
                        // real server offset before retrying. A 502 from a reverse proxy can
                        // occur AFTER the server committed the chunk, making a same-offset
                        // retry produce a 409 Conflict. If the server is already past this
                        // chunk, skip the PATCH and advance the local offset.
                        var sv = await QueryTusOffsetAsync(uploadUrl, tusClient, ct);
                        if (sv >= offset + bytesRead)
                        {
                            _logger.LogDebug(
                                "SeekStreaming: server already has chunk (serverOffset={Sv} >= {Expected}), skipping retry",
                                sv, offset + bytesRead);
                            serverOffsetOverride = sv;
                            lastFailStatus = null;
                            break;
                        }

                        await Task.Delay(delay, ct);
                    }

                    using var patchReq = new HttpRequestMessage(HttpMethod.Patch, uploadUrl);
                    patchReq.Headers.TryAddWithoutValidation("Tus-Resumable", "1.0.0");
                    patchReq.Headers.TryAddWithoutValidation("Upload-Offset", offset.ToString());
                    patchReq.Content = new ByteArrayContent(buffer, 0, bytesRead);
                    patchReq.Content.Headers.ContentType =
                        MediaTypeHeaderValue.Parse("application/offset+octet-stream");
                    patchReq.Content.Headers.ContentLength = bytesRead;

                    using var patchResp = await tusClient.SendAsync(patchReq, ct);

                    if (patchResp.IsSuccessStatusCode)
                    {
                        lastFailStatus = null;
                        break;
                    }

                    lastFailStatus = patchResp.StatusCode;

                    // Only retry on transient server errors — fail fast on auth/client errors.
                    if (patchResp.StatusCode is not (
                        System.Net.HttpStatusCode.BadGateway or           // 502
                        System.Net.HttpStatusCode.ServiceUnavailable or   // 503
                        System.Net.HttpStatusCode.GatewayTimeout or       // 504
                        System.Net.HttpStatusCode.TooManyRequests))       // 429
                    {
                        patchResp.EnsureSuccessStatusCode(); // throws for non-transient errors
                    }
                }

                if (lastFailStatus is not null)
                    throw new HttpRequestException(
                        $"SeekStreaming tus PATCH failed after {MaxChunkRetries} retries — last status: {(int)lastFailStatus} {lastFailStatus}");

                offset = serverOffsetOverride ?? (offset + bytesRead);

                _logger.LogDebug(
                    "SeekStreaming tus chunk: {Offset}/{Total} bytes ({Pct:F0}%)",
                    offset, fileSize, 100.0 * offset / fileSize);
            }
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            // Per-attempt: el caller cae al siguiente candidato/katanime y loguea el
            // fallo real UNA sola vez por episodio. Los 403 de CDNs (mp4upload port
            // 183 bloquea IPs de datacenter) son esperados → Debug, sin stacktrace,
            // para no llenar el log de "errores" en una corrida que igual termina OK.
            _logger.LogDebug(
                "SeekStreaming: tus upload failed (ep={EpisodeId} via={Provider}): {Error}",
                episodeId, provider, ex.Message);
            return null;
        }
        finally
        {
            if (tempFilePath is not null)
                try { File.Delete(tempFilePath); } catch { /* best-effort cleanup */ }
        }

        _logger.LogInformation(
            "SeekStreaming tus transfer complete (ep={EpisodeId} via={Provider} {Size} bytes) for {Url} — polling for transcoding",
            episodeId, provider, fileSize, directVideoUrl);

        // ─── Step 5: poll video/manage until Active ───────────────
        // El transcoding escala con el tamaño (~1 min por cada 25 MB) pero acotado:
        // piso = override del caller o el configurado; tope = cap configurado. Así
        // un archivo trabado no espera 15-20 min estancando la cola paralela.
        var floor = pollTimeoutMinutes > 0 ? pollTimeoutMinutes : _transcodeFloorMinutes;
        var cap = Math.Max(floor, _transcodeCapMinutes);
        var sizeBasedMinutes = (int)(fileSize / (25L * 1024 * 1024));
        var effectiveTimeout = Math.Clamp(Math.Max(floor, sizeBasedMinutes), floor, cap);
        var videoId = await PollForVideoAsync(filename, effectiveTimeout, episodeId, provider, directVideoUrl, ct);
        if (videoId is null)
        {
            // Esperado para algunos archivos; el episodio cae al siguiente candidato
            // o a katanime, as\u00ed que Information (no Warning) para no alarmar.
            _logger.LogInformation(
                "SeekStreaming: transcoding timed out after {Timeout} min (ep={EpisodeId} via={Provider} filename={Filename}) \u2014 probando fallback",
                effectiveTimeout, episodeId, provider, filename);
            return null;
        }

        var embedUrl = GetEmbedUrl(videoId);
        _logger.LogInformation(
            "\u2705 SeekStreaming upload complete: ep={EpisodeId} via={Provider} filename={Filename} videoId={VideoId} embed={EmbedUrl}",
            episodeId, provider, filename, videoId, embedUrl);
        return embedUrl;
    }

    private async Task<string?> PollForVideoAsync(
        string filename, int timeoutMinutes,
        Guid? episodeId, string? provider, string? sourceUrl,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(timeoutMinutes);
        const int PollIntervalMs = 15_000; // 15 s — transcoding is fast for most files
        // Match by full name OR without extension — SeekStreaming API may strip .mp4 from the stored name.
        var filenameNoExt = Path.GetFileNameWithoutExtension(filename);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(PollIntervalMs, ct);
            try
            {
                // limit=100 — ensure all recently uploaded videos are in the window even with
                // 6+ parallel uploads on an account that already has many videos.
                using var res = await _http.GetAsync($"{_baseUrl}/api/v1/video/manage?limit=100", ct);
                if (!res.IsSuccessStatusCode) continue;

                var json = await res.Content.ReadAsStringAsync(ct);
                var list = JsonSerializer.Deserialize<VideoManageResponse>(json, JsonOpts);

                var match = list?.Data?.FirstOrDefault(v =>
                    filename.Equals(v.Name, StringComparison.OrdinalIgnoreCase) ||
                    filenameNoExt.Equals(v.Name, StringComparison.OrdinalIgnoreCase));

                if (match is null)
                {
                    // Log what the API actually returned so we can diagnose name-format mismatches.
                    var names = list?.Data?.Select(v => v.Name ?? "(null)").ToArray() ?? [];
                    _logger.LogDebug(
                        "SeekStreaming poll: looking for '{Filename}' (or '{FilenameNoExt}') ep={EpisodeId} via={Provider} — {Count} videos in list: [{Names}]",
                        filename, filenameNoExt, episodeId, provider, names.Length, string.Join(", ", names.Take(10)));
                    continue;
                }

                _logger.LogDebug(
                    "SeekStreaming poll: found {Name} id={Id} status={Status}",
                    match.Name, match.Id, match.Status);

                if (string.Equals(match.Status, "Active", StringComparison.OrdinalIgnoreCase))
                    return match.Id;

                // Treat terminal failure states as immediate exit
                if (string.Equals(match.Status, "Deleted", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "SeekStreaming: video {Name} was Deleted during transcoding (ep={EpisodeId} via={Provider})",
                        filename, episodeId, provider);
                    return null;
                }
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SeekStreaming poll threw, will retry");
            }
        }

        return null;
    }

    /// <summary>Builds the embed URL from a video ID.</summary>
    public string GetEmbedUrl(string videoId) => $"https://sheicobanime.seekplayer.me/#{videoId}";

    // Browser UA sent on all CDN requests so mxcontent.net (mixdrop CDN) returns 200.
    // Tested: HEAD/GET with UA → 200; without UA → 403 regardless of Referer.
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    /// <summary>
    /// Tries HEAD first (fast); if it fails or returns no Content-Length, falls back
    /// to GET Range:bytes=0-0 and reads the total size from Content-Range.
    /// Returns 0 if both strategies fail.
    /// </summary>
    private async Task<long> GetFileSizeAsync(HttpClient client, string url, string? referer, CancellationToken ct)
    {
        // Strategy 1: HEAD
        // Explicit UA on the request (belt+suspenders alongside the seek-download client's
        // DefaultRequestHeaders) — mxcontent.net CDN returns 403 without a browser UA.
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            if (!string.IsNullOrEmpty(referer))
                req.Headers.TryAddWithoutValidation("Referer", referer);
            else if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                req.Headers.TryAddWithoutValidation("Referer", $"{uri.Scheme}://{uri.Host}/");
            using var resp = await client.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentLength is long len and > 0)
                return len;
            if (!resp.IsSuccessStatusCode)
                _logger.LogDebug(
                    "SeekStreaming: HEAD {Status} for {Url} — trying Range GET",
                    (int)resp.StatusCode, url);
        }
        catch (Exception ex) when (ex is not TaskCanceledException { CancellationToken.IsCancellationRequested: true })
        {
            _logger.LogDebug(ex, "SeekStreaming: HEAD failed for {Url}, trying Range GET", url);
        }

        // Strategy 2: GET Range:bytes=0-0 → read Content-Range: bytes 0-0/TOTAL
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            if (!string.IsNullOrEmpty(referer))
                req.Headers.TryAddWithoutValidation("Referer", referer);
            else if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                req.Headers.TryAddWithoutValidation("Referer", $"{uri.Scheme}://{uri.Host}/");
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            // 206 Partial Content: Content-Range header has the total
            if (resp.StatusCode == System.Net.HttpStatusCode.PartialContent)
            {
                var cr = resp.Content.Headers.ContentRange;
                if (cr?.Length is long total and > 0)
                    return total;
            }
            // 200 OK (server ignores Range): Content-Length is the full file size
            if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentLength is long cl and > 0)
                return cl;
            if (!resp.IsSuccessStatusCode)
                _logger.LogDebug(
                    "SeekStreaming: Range GET {Status} for {Url}",
                    (int)resp.StatusCode, url);
        }
        catch (Exception ex) when (ex is not TaskCanceledException { CancellationToken.IsCancellationRequested: true })
        {
            _logger.LogDebug(ex, "SeekStreaming: Range GET also failed for {Url}", url);
        }

        // Strategy 3: plain GET, leer Content-Length de los headers y abortar.
        // mixdrop (mxcontent.net) bloquea HEAD y Range pero el GET normal trae
        // Content-Length — con ResponseHeadersRead no descargamos el cuerpo.
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            if (!string.IsNullOrEmpty(referer))
                req.Headers.TryAddWithoutValidation("Referer", referer);
            else if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                req.Headers.TryAddWithoutValidation("Referer", $"{uri.Scheme}://{uri.Host}/");
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentLength is long cl3 and > 0)
                return cl3;
            if (!resp.IsSuccessStatusCode)
                _logger.LogDebug(
                    "SeekStreaming: plain GET {Status} for {Url}",
                    (int)resp.StatusCode, url);
        }
        catch (Exception ex) when (ex is not TaskCanceledException { CancellationToken.IsCancellationRequested: true })
        {
            _logger.LogDebug(ex, "SeekStreaming: plain GET also failed for {Url}", url);
        }

        return 0;
    }

    /// <summary>
    /// Último recurso cuando ningún header revela el tamaño: descarga el archivo
    /// completo a un temp file para medirlo (tus exige Upload-Length por
    /// adelantado). El caller es responsable de borrar el archivo.
    /// Devuelve null si la descarga falla o viene vacía.
    /// </summary>
    private async Task<string?> SpoolToTempFileAsync(
        HttpClient client, string url, string? referer,
        Guid? episodeId, string? provider, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"seekspool_{Guid.NewGuid():N}.mp4");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            if (!string.IsNullOrEmpty(referer))
                req.Headers.TryAddWithoutValidation("Referer", referer);
            else if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                req.Headers.TryAddWithoutValidation("Referer", $"{uri.Scheme}://{uri.Host}/");

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(path))
            {
                await src.CopyToAsync(dst, 1 << 20, ct);
            }

            var size = new FileInfo(path).Length;
            if (size <= 0)
            {
                try { File.Delete(path); } catch { }
                return null;
            }

            _logger.LogInformation(
                "SeekStreaming: spooled {Size} bytes to temp file (ep={EpisodeId} via={Provider})",
                size, episodeId, provider);
            return path;
        }
        catch (Exception ex) when (ex is not TaskCanceledException { CancellationToken.IsCancellationRequested: true })
        {
            // Per-attempt: con fallback. Debug para no ensuciar el log con providers
            // que igual se recuperan por otro candidato.
            _logger.LogDebug(ex,
                "SeekStreaming: temp spool failed (ep={EpisodeId} via={Provider}) for {Url}",
                episodeId, provider, url);
            try { File.Delete(path); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Sends a tus HEAD request to retrieve the current server-side Upload-Offset.
    /// Returns -1 if the check fails so callers treat it as "unknown — retry".
    /// </summary>
    private static async Task<long> QueryTusOffsetAsync(
        string uploadUrl, HttpClient tusClient, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, uploadUrl);
            req.Headers.TryAddWithoutValidation("Tus-Resumable", "1.0.0");
            using var resp = await tusClient.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode &&
                resp.Headers.TryGetValues("Upload-Offset", out var vals) &&
                long.TryParse(vals.First(), out var sv))
                return sv;
        }
        catch { }
        return -1L;
    }

    private static string BuildTusMetadata(string accessToken, string filename, string mimeType)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        return $"accessToken {B64(accessToken)},filename {B64(filename)},filetype {B64(mimeType)}";
    }

    // ── JSON models ──────────────────────────────────────────────────────────

    private sealed record TusCredentials(
        [property: JsonPropertyName("tusUrl")] string? TusUrl,
        [property: JsonPropertyName("accessToken")] string? AccessToken);

    private sealed record VideoManageResponse(
        [property: JsonPropertyName("data")] VideoManageItem[]? Data);

    private sealed record VideoManageItem(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("status")] string? Status);
}
