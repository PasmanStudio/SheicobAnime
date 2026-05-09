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
/// Upload flow (async):
///   1. POST /api/v1/video/advance-upload  { url, name } → { id: "task_id" }
///   2. Poll GET /api/v1/video/advance-upload/{task_id} until status == "Completed"
///   3. Task response contains videos[] with video IDs
 ///   4. Embed URL: https://sheicobanime.seekplayer.me/#{videoId}
///
/// Config key: "SeekStreaming:ApiKey"  (env var: SEEKSTREAMING__APIKEY)
/// </summary>
public sealed class SeekStreamingClient
{
    private readonly HttpClient _http;
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

        var apiKey = config["SeekStreaming:ApiKey"]
            ?? throw new InvalidOperationException(
                "SeekStreaming:ApiKey is not configured. Set env var SEEKSTREAMING__APIKEY.");

        _baseUrl = config["SeekStreaming:BaseUrl"]?.TrimEnd('/') ?? "https://seekstreaming.com";

        _http = httpFactory.CreateClient("seekstreaming");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("api-token", apiKey);
    }

    /// <summary>
    /// Submits a remote URL upload task and waits up to <paramref name="pollTimeoutMinutes"/> minutes
    /// for transcoding to complete. Returns the embed URL on success, or null on failure/timeout.
    /// </summary>
    public async Task<string?> UploadFromUrlAsync(
        string videoUrl,
        string? name = null,
        int pollTimeoutMinutes = 10,
        CancellationToken ct = default)
    {
        // Step 1: submit the task
        var taskId = await SubmitTaskAsync(videoUrl, name, ct);
        if (taskId is null) return null;

        _logger.LogInformation(
            "SeekStreaming task submitted: taskId={TaskId} for {VideoUrl}", taskId, videoUrl);

        // Step 2: poll until Completed or timeout
        var videoId = await PollTaskAsync(taskId, pollTimeoutMinutes, ct);
        if (videoId is null)
        {
            _logger.LogWarning(
                "SeekStreaming task {TaskId} did not complete within {Timeout} minutes for {VideoUrl}",
                taskId, pollTimeoutMinutes, videoUrl);
            return null;
        }

        var embedUrl = GetEmbedUrl(videoId);
        _logger.LogInformation(
            "SeekStreaming upload complete: taskId={TaskId} videoId={VideoId} embedUrl={EmbedUrl}",
            taskId, videoId, embedUrl);
        return embedUrl;
    }

    private async Task<string?> SubmitTaskAsync(string videoUrl, string? name, CancellationToken ct)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var bodyObj = new { url = videoUrl, name = name ?? videoUrl };
                var json = JsonSerializer.Serialize(bodyObj);
                // SeekStreaming rejects Content-Type: application/json; charset=utf-8
                // (PostAsJsonAsync adds charset automatically) — use StringContent + explicit MediaType
                using var content = new StringContent(json, Encoding.UTF8);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var res = await _http.PostAsync(
                    $"{_baseUrl}/api/v1/video/advance-upload", content, ct);

                if (!res.IsSuccessStatusCode)
                {
                    var err = await res.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning(
                        "SeekStreaming submit returned {Status} (attempt {Attempt}/{Max}): {Body}",
                        (int)res.StatusCode, attempt, maxAttempts,
                        err.Length > 200 ? err[..200] : err);

                    if (attempt < maxAttempts)
                        await Task.Delay(3_000 * attempt, ct);
                    continue;
                }

                var responseJson = await res.Content.ReadAsStringAsync(ct);
                var response = JsonSerializer.Deserialize<AdvanceUploadCreateResponse>(responseJson, JsonOpts);

                if (!string.IsNullOrWhiteSpace(response?.Id))
                    return response.Id;

                _logger.LogWarning("SeekStreaming submit: no task id in response (attempt {Attempt})", attempt);
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SeekStreaming submit attempt {Attempt}/{Max} threw", attempt, maxAttempts);
            }

            if (attempt < maxAttempts)
                await Task.Delay(3_000 * attempt, ct);
        }

        _logger.LogError("SeekStreaming submit failed after {Max} attempts for {VideoUrl}",
            maxAttempts, videoUrl);
        return null;
    }

    private async Task<string?> PollTaskAsync(string taskId, int timeoutMinutes, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(timeoutMinutes);
        const int pollIntervalMs = 30_000; // 30 seconds between polls

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(pollIntervalMs, ct);

            try
            {
                using var res = await _http.GetAsync(
                    $"{_baseUrl}/api/v1/video/advance-upload/{taskId}", ct);

                if (!res.IsSuccessStatusCode)
                {
                    _logger.LogDebug("SeekStreaming poll {TaskId}: HTTP {Status}", taskId, (int)res.StatusCode);
                    continue;
                }

                var taskJson = await res.Content.ReadAsStringAsync(ct);
                var task = JsonSerializer.Deserialize<AdvanceUploadTaskResponse>(taskJson, JsonOpts);
                _logger.LogDebug("SeekStreaming poll {TaskId}: status={Status}", taskId, task?.Status);

                if (task?.Status == "Completed" && task.Videos?.Length > 0)
                    return task.Videos[0]; // use first video ID

                if (task?.Status == "Failed" || task?.Status == "Canceled")
                {
                    _logger.LogWarning(
                        "SeekStreaming task {TaskId} ended with status={Status} error={Error}",
                        taskId, task.Status, task.Error);
                    return null;
                }
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SeekStreaming poll {TaskId} threw, will retry", taskId);
            }
        }

        return null; // timeout
    }

    /// <summary>Builds the embed URL from a video ID.</summary>
    public string GetEmbedUrl(string videoId) => $"https://sheicobanime.seekplayer.me/#{videoId}";

    // ── JSON models ──────────────────────────────────────────────────────────

    private sealed record AdvanceUploadCreateResponse(
        [property: JsonPropertyName("id")] string? Id);

    private sealed record AdvanceUploadTaskResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("videos")] string[]? Videos);
}
