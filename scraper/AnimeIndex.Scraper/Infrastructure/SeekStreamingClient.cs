using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure;

/// <summary>
/// HTTP client for the SeekStreaming API.
/// Supports remote URL upload: passes a direct video URL and SeekStreaming fetches it.
/// API docs pattern: GET /api/upload/url?key=KEY&amp;url=VIDEO_URL → { status:200, result:{ filecode:"xxx" } }
/// Embed URL: https://seekstreaming.com/e/{filecode}
///
/// The API key is read from configuration key "SeekStreaming:ApiKey".
/// NEVER hardcode the key — always inject via env var SEEKSTREAMING__APIKEY.
/// </summary>
public sealed class SeekStreamingClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
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
        _http = httpFactory.CreateClient("seekstreaming");

        _apiKey = config["SeekStreaming:ApiKey"]
            ?? throw new InvalidOperationException(
                "SeekStreaming:ApiKey is not configured. Set env var SEEKSTREAMING__APIKEY.");

        _baseUrl = config["SeekStreaming:BaseUrl"]?.TrimEnd('/') ?? "https://seekstreaming.com";
    }

    /// <summary>
    /// Initiates a remote URL upload. SeekStreaming fetches the video from <paramref name="videoUrl"/>.
    /// Returns the filecode (e.g. "fb5asfuj2snh") on success, or null if the upload fails after retries.
    /// The filecode is immediately available even while the video is still transcoding.
    /// </summary>
    public async Task<string?> UploadFromUrlAsync(string videoUrl, CancellationToken ct = default)
    {
        const int maxAttempts = 3;
        const int backoffMs = 3_000;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var url = $"{_baseUrl}/api/upload/url?key={Uri.EscapeDataString(_apiKey)}&url={Uri.EscapeDataString(videoUrl)}";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var res = await _http.SendAsync(req, ct);

                if (!res.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "SeekStreaming upload/url returned {Status} (attempt {Attempt}/{Max}) for {VideoUrl}",
                        (int)res.StatusCode, attempt, maxAttempts, videoUrl);

                    if (attempt < maxAttempts)
                        await Task.Delay(backoffMs * attempt, ct);

                    continue;
                }

                var body = await res.Content.ReadAsStringAsync(ct);
                var response = JsonSerializer.Deserialize<SeekStreamingUploadResponse>(body, JsonOpts);

                if (response?.Status == 200 && !string.IsNullOrWhiteSpace(response.Result?.Filecode))
                {
                    _logger.LogInformation(
                        "SeekStreaming upload succeeded: filecode={Filecode} for {VideoUrl}",
                        response.Result.Filecode, videoUrl);
                    return response.Result.Filecode;
                }

                _logger.LogWarning(
                    "SeekStreaming upload/url unexpected response (attempt {Attempt}/{Max}): {Body}",
                    attempt, maxAttempts, body.Length > 200 ? body[..200] : body);
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                return null; // job cancelled — stop quietly
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SeekStreaming upload attempt {Attempt}/{Max} threw for {VideoUrl}",
                    attempt, maxAttempts, videoUrl);
            }

            if (attempt < maxAttempts)
                await Task.Delay(backoffMs * attempt, ct);
        }

        _logger.LogError("SeekStreaming upload failed after {Max} attempts for {VideoUrl}", maxAttempts, videoUrl);
        return null;
    }

    /// <summary>
    /// Builds the embed URL from a filecode.
    /// Example: filecode "fb5asfuj2snh" → "https://seekstreaming.com/e/fb5asfuj2snh"
    /// </summary>
    public string GetEmbedUrl(string filecode) => $"{_baseUrl}/e/{filecode}";

    // ── JSON models ──────────────────────────────────────────────────────────

    private sealed class SeekStreamingUploadResponse
    {
        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("result")]
        public SeekStreamingUploadResult? Result { get; set; }
    }

    private sealed class SeekStreamingUploadResult
    {
        [JsonPropertyName("filecode")]
        public string? Filecode { get; set; }
    }
}
