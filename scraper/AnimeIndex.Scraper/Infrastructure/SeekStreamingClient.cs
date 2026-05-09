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
        string? name = null,
        int pollTimeoutMinutes = 10,
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
            _logger.LogWarning(ex, "SeekStreaming: failed to get tus credentials");
            return null;
        }

        if (creds is null || string.IsNullOrEmpty(creds.TusUrl) || string.IsNullOrEmpty(creds.AccessToken))
        {
            _logger.LogWarning("SeekStreaming: invalid tus credentials response");
            return null;
        }

        // ─── Step 2: HEAD the source URL → file size ──────────────
        long fileSize;
        using (var dlClient = _httpFactory.CreateClient("seek-download"))
        {
            try
            {
                using var headReq = new HttpRequestMessage(HttpMethod.Head, directVideoUrl);
                using var headResp = await dlClient.SendAsync(headReq, ct);
                headResp.EnsureSuccessStatusCode();
                fileSize = headResp.Content.Headers.ContentLength
                    ?? throw new InvalidOperationException($"No Content-Length from {directVideoUrl}");
            }
            catch (Exception ex) when (ex is not TaskCanceledException { CancellationToken.IsCancellationRequested: true })
            {
                _logger.LogWarning(ex, "SeekStreaming: HEAD failed for {Url}", directVideoUrl);
                return null;
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
            _logger.LogWarning(ex, "SeekStreaming: failed to create tus upload slot");
            return null;
        }

        _logger.LogInformation(
            "SeekStreaming tus upload started: filename={Filename} size={Size} url={Url}",
            filename, fileSize, directVideoUrl);

        // ─── Step 4: stream download → tus PATCH in 50 MB chunks ──
        const long ChunkSize = 52_428_800L; // 50 MB — SeekStreaming recommended
        try
        {
            using var dlClient = _httpFactory.CreateClient("seek-download");
            using var tusClient = _httpFactory.CreateClient("seek-tus");

            using var downloadResp = await dlClient.GetAsync(
                directVideoUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            downloadResp.EnsureSuccessStatusCode();

            await using var downloadStream = await downloadResp.Content.ReadAsStreamAsync(ct);
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

                using var patchReq = new HttpRequestMessage(HttpMethod.Patch, uploadUrl);
                patchReq.Headers.TryAddWithoutValidation("Tus-Resumable", "1.0.0");
                patchReq.Headers.TryAddWithoutValidation("Upload-Offset", offset.ToString());
                patchReq.Content = new ByteArrayContent(buffer, 0, bytesRead);
                patchReq.Content.Headers.ContentType =
                    MediaTypeHeaderValue.Parse("application/offset+octet-stream");
                patchReq.Content.Headers.ContentLength = bytesRead;

                using var patchResp = await tusClient.SendAsync(patchReq, ct);
                patchResp.EnsureSuccessStatusCode();
                offset += bytesRead;

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
            _logger.LogWarning(ex, "SeekStreaming: tus upload failed for {Url}", directVideoUrl);
            return null;
        }

        _logger.LogInformation(
            "SeekStreaming tus transfer complete ({Size} bytes) for {Url} — polling for transcoding",
            fileSize, directVideoUrl);

        // ─── Step 5: poll video/manage until Active ───────────────
        var videoId = await PollForVideoAsync(filename, pollTimeoutMinutes, ct);
        if (videoId is null)
        {
            _logger.LogWarning(
                "SeekStreaming: video {Filename} not Active after {Timeout} min",
                filename, pollTimeoutMinutes);
            return null;
        }

        var embedUrl = GetEmbedUrl(videoId);
        _logger.LogInformation(
            "SeekStreaming upload complete: filename={Filename} videoId={VideoId} embed={EmbedUrl}",
            filename, videoId, embedUrl);
        return embedUrl;
    }

    private async Task<string?> PollForVideoAsync(string filename, int timeoutMinutes, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(timeoutMinutes);
        const int PollIntervalMs = 15_000; // 15 s — transcoding is fast for most files

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(PollIntervalMs, ct);
            try
            {
                using var res = await _http.GetAsync($"{_baseUrl}/api/v1/video/manage?limit=20", ct);
                if (!res.IsSuccessStatusCode) continue;

                var json = await res.Content.ReadAsStringAsync(ct);
                var list = JsonSerializer.Deserialize<VideoManageResponse>(json, JsonOpts);

                var match = list?.Data?.FirstOrDefault(v =>
                    filename.Equals(v.Name, StringComparison.OrdinalIgnoreCase));

                if (match is null) continue;

                _logger.LogDebug(
                    "SeekStreaming poll: found {Name} id={Id} status={Status}",
                    match.Name, match.Id, match.Status);

                if (string.Equals(match.Status, "Active", StringComparison.OrdinalIgnoreCase))
                    return match.Id;

                // Treat terminal failure states as immediate exit
                if (string.Equals(match.Status, "Deleted", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("SeekStreaming: video {Name} was Deleted during transcoding", filename);
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
