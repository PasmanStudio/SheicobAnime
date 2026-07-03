using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>
/// Wraps the Meta Graph API for Instagram content publishing.
/// Requires a long-lived Instagram User Access Token (valid 60 days).
///
/// Token refresh: call RefreshTokenAsync() proactively before day 60.
/// Store the returned token in INSTAGRAM_ACCESS_TOKEN secret.
/// </summary>
public class MetaGraphApiClient(
    IHttpClientFactory httpClientFactory,
    InstagramSettings settings,
    ILogger<MetaGraphApiClient> logger)
{
    private HttpClient Http => httpClientFactory.CreateClient("instagram-graph");

    // Instagram Graph API for Business uses graph.facebook.com, NOT graph.instagram.com
    // (graph.instagram.com is the deprecated Basic Display API that uses a different token type)
    private string BaseUrl => $"https://graph.facebook.com/{settings.ApiVersion}";

    // ── Image hosting ─────────────────────────────────────────────────

    /// <summary>
    /// Uploads image bytes to a public host and returns the public HTTPS URL that
    /// the Meta Graph API will fetch. Prefers Cloudinary (key-authenticated, so it
    /// is NOT subject to the shared GitHub-Actions-IP throttle that breaks imgbb);
    /// falls back to imgbb.com only when Cloudinary is not configured.
    /// </summary>
    public Task<string> UploadImageAsync(
        byte[] imageBytes, string fileName, CancellationToken ct = default)
        => settings.CloudinaryConfigured
            ? UploadImageToCloudinaryAsync(imageBytes, fileName, ct)
            : UploadImageToImgBbAsync(imageBytes, fileName, ct);

    // Lazily built per scope (MetaGraphApiClient is scoped; the process is short-lived).
    private Cloudinary? _cloudinary;
    private Cloudinary Cloudinary => _cloudinary ??= new Cloudinary(
        new Account(settings.CloudinaryCloudName, settings.CloudinaryApiKey, settings.CloudinaryApiSecret));

    /// <summary>
    /// Uploads image bytes to Cloudinary and returns the secure (HTTPS) URL.
    /// Cloudinary authenticates by API key, so unlike imgbb it is immune to the
    /// shared GitHub-Actions-IP rate limiting that broke the imgbb path.
    /// </summary>
    private async Task<string> UploadImageToCloudinaryAsync(
        byte[] imageBytes, string fileName, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(imageBytes);
        var result = await Cloudinary.UploadAsync(new ImageUploadParams
        {
            File      = new FileDescription(fileName, ms),
            PublicId  = Path.GetFileNameWithoutExtension(fileName),
            Folder    = string.IsNullOrWhiteSpace(settings.CloudinaryFolder) ? null : settings.CloudinaryFolder,
            Overwrite = true,
        }, ct);

        if (result.Error is not null)
            throw new InvalidOperationException($"Cloudinary upload failed: {result.Error.Message}");

        var url = result.SecureUrl?.ToString()
            ?? throw new InvalidOperationException("Cloudinary response missing secure_url");

        logger.LogDebug("Uploaded {File} to Cloudinary: {Url}", fileName, url);
        return url;
    }

    /// <summary>
    /// LEGACY fallback: uploads image bytes to imgbb.com. imgbb throttles the
    /// shared GitHub-Actions IP (code 100 "Rate limit reached") regardless of our
    /// own volume — used only when Cloudinary is not configured.
    /// </summary>
    private async Task<string> UploadImageToImgBbAsync(
        byte[] imageBytes, string fileName, CancellationToken ct = default)
    {
        var base64 = Convert.ToBase64String(imageBytes);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(settings.ImgBbApiKey), "key");
        form.Add(new StringContent(fileName), "name");
        form.Add(new StringContent(base64), "image");

        var resp = await Http.PostAsync("https://api.imgbb.com/1/upload", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"imgbb upload failed ({resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var url = doc.RootElement
            .GetProperty("data")
            .GetProperty("url")
            .GetString()
            ?? throw new InvalidOperationException("imgbb response missing data.url");

        logger.LogDebug("Uploaded {File} to imgbb: {Url}", fileName, url);
        return url;
    }

    // ── Video hosting ─────────────────────────────────────────────────

    /// <summary>
    /// Uploads video bytes (MP4) to Cloudinary and returns the public HTTPS URL
    /// that Meta will fetch. Video requires Cloudinary — imgbb no hostea video.
    /// </summary>
    public async Task<string> UploadVideoAsync(
        byte[] videoBytes, string fileName, CancellationToken ct = default)
    {
        if (!settings.CloudinaryConfigured)
            throw new InvalidOperationException(
                "Video upload requires Cloudinary (imgbb only hosts images) — set Instagram__Cloudinary* secrets");

        using var ms = new MemoryStream(videoBytes);
        var result = await Cloudinary.UploadAsync(new VideoUploadParams
        {
            File      = new FileDescription(fileName, ms),
            PublicId  = Path.GetFileNameWithoutExtension(fileName),
            Folder    = string.IsNullOrWhiteSpace(settings.CloudinaryFolder) ? null : settings.CloudinaryFolder,
            Overwrite = true,
        }, ct);

        if (result.Error is not null)
            throw new InvalidOperationException($"Cloudinary video upload failed: {result.Error.Message}");

        var url = result.SecureUrl?.ToString()
            ?? throw new InvalidOperationException("Cloudinary response missing secure_url");

        logger.LogDebug("Uploaded {File} to Cloudinary: {Url}", fileName, url);
        return url;
    }

    // ── Reels ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a Reel container (media_type=REELS). The video must already be
    /// hosted on a public HTTPS URL. share_to_feed=true also surfaces the Reel
    /// in the profile feed. Video processing is async — poll with
    /// WaitForContainerReadyAsync using a generous timeout (~5 min).
    /// </summary>
    public async Task<string> CreateReelContainerAsync(
        string videoUrl, string? caption, bool shareToFeed = true, CancellationToken ct = default)
    {
        var fields = new Dictionary<string, string>
        {
            ["media_type"]    = "REELS",
            ["video_url"]     = videoUrl,
            ["share_to_feed"] = shareToFeed ? "true" : "false",
            ["access_token"]  = settings.AccessToken
        };
        if (!string.IsNullOrWhiteSpace(caption))
            fields["caption"] = caption;

        using var form = new FormUrlEncodedContent(fields);
        var resp = await Http.PostAsync($"{BaseUrl}/{settings.IgUserId}/media", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"CreateReel failed ({resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Reel container response missing id");
    }

    /// <summary>
    /// Creates a video Story container with a link sticker (media_type=STORIES +
    /// video_url). Video stories: 3–60 s, máx 100MB, mismos códecs que Reels.
    /// </summary>
    public async Task<string> CreateVideoStoryContainerAsync(
        string videoUrl, string linkStickerUrl, CancellationToken ct = default)
    {
        var fields = new Dictionary<string, string>
        {
            ["media_type"]       = "STORIES",
            ["video_url"]        = videoUrl,
            ["link_sticker_url"] = linkStickerUrl,
            ["access_token"]     = settings.AccessToken
        };

        using var form = new FormUrlEncodedContent(fields);
        var resp = await Http.PostAsync($"{BaseUrl}/{settings.IgUserId}/media", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"CreateVideoStory failed ({resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Video story container response missing id");
    }

    // ── Carousel workflow ─────────────────────────────────────────────

    /// <summary>
    /// Creates a carousel child (item) container for one image.
    /// Must be called for each image before creating the carousel container.
    /// </summary>
    public async Task<string> CreateCarouselItemContainerAsync(
        string imageUrl, CancellationToken ct = default)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["image_url"]        = imageUrl,
            ["is_carousel_item"] = "true",
            ["access_token"]     = settings.AccessToken
        });

        var resp = await Http.PostAsync($"{BaseUrl}/{settings.IgUserId}/media", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"CreateCarouselItem failed ({resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Carousel item response missing id");
    }

    /// <summary>
    /// Creates the carousel (parent) container referencing pre-created child containers.
    /// Requires at least 2 and at most 10 child IDs.
    /// </summary>
    public async Task<string> CreateCarouselContainerAsync(
        IReadOnlyList<string> childContainerIds,
        string? caption,
        CancellationToken ct = default)
    {
        if (childContainerIds.Count < 2)
            throw new ArgumentException("Instagram carousel requires at least 2 items", nameof(childContainerIds));
        if (childContainerIds.Count > 10)
            throw new ArgumentException("Instagram carousel supports at most 10 items", nameof(childContainerIds));

        var fields = new Dictionary<string, string>
        {
            ["media_type"]   = "CAROUSEL",
            ["children"]     = string.Join(",", childContainerIds),
            ["access_token"] = settings.AccessToken
        };
        if (!string.IsNullOrWhiteSpace(caption))
            fields["caption"] = caption;

        using var form = new FormUrlEncodedContent(fields);
        var resp = await Http.PostAsync($"{BaseUrl}/{settings.IgUserId}/media", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"CreateCarouselContainer failed ({resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Carousel container response missing id");

        logger.LogInformation("Created carousel container {Id} with {Count} items", id, childContainerIds.Count);
        return id;
    }

    /// <summary>
    /// Creates a single (non-carousel) feed post container.
    /// Use when there is only 1 new episode (carousel requires 2+).
    /// </summary>
    public async Task<string> CreateSingleImageContainerAsync(
        string imageUrl, string? caption, CancellationToken ct = default)
    {
        var fields = new Dictionary<string, string>
        {
            ["image_url"]    = imageUrl,
            ["access_token"] = settings.AccessToken
        };
        if (!string.IsNullOrWhiteSpace(caption))
            fields["caption"] = caption;

        using var form = new FormUrlEncodedContent(fields);
        var resp = await Http.PostAsync($"{BaseUrl}/{settings.IgUserId}/media", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"CreateSingleImage failed ({resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Single image container response missing id");
    }

    /// <summary>
    /// Creates an Instagram Story container with a link sticker.
    /// The story image should be 1080×1920 (9:16). Requires media_type=STORIES.
    /// </summary>
    public async Task<string> CreateStoryContainerAsync(
        string imageUrl, string linkStickerUrl, CancellationToken ct = default)
    {
        var fields = new Dictionary<string, string>
        {
            ["image_url"]        = imageUrl,
            ["media_type"]       = "STORIES",
            ["link_sticker_url"] = linkStickerUrl,
            ["access_token"]     = settings.AccessToken
        };

        using var form = new FormUrlEncodedContent(fields);
        var resp = await Http.PostAsync($"{BaseUrl}/{settings.IgUserId}/media", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"CreateStory failed ({resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Story container response missing id");
    }

    // ── Shared publish flow ───────────────────────────────────────────

    /// <summary>
    /// Polls the container until its status is FINISHED (default ~90 s — pass a
    /// longer timeout for video: Meta puede tardar varios minutos en procesarlo).
    /// Throws if it reaches ERROR or EXPIRED.
    /// </summary>
    public async Task WaitForContainerReadyAsync(
        string containerId, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var url = $"{BaseUrl}/{containerId}"
                + $"?fields=status_code&access_token={Uri.EscapeDataString(settings.AccessToken)}";

        var attempts = (int)Math.Ceiling((timeout ?? TimeSpan.FromSeconds(90)).TotalSeconds / 5);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) continue;

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.GetProperty("status_code").GetString();

            logger.LogDebug("Container {Id} status: {Status}", containerId, status);

            switch (status)
            {
                case "FINISHED":   return;
                case "ERROR":
                case "EXPIRED":
                    throw new InvalidOperationException(
                        $"Container {containerId} reached terminal status: {status}");
            }
        }

        throw new TimeoutException(
            $"Container {containerId} did not finish within {(timeout ?? TimeSpan.FromSeconds(90)).TotalSeconds:F0} seconds");
    }

    /// <summary>Publishes a ready container and returns the published IG Media ID.</summary>
    public async Task<string> PublishContainerAsync(string containerId, CancellationToken ct = default)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["creation_id"]  = containerId,
            ["access_token"] = settings.AccessToken
        });

        var resp = await Http.PostAsync($"{BaseUrl}/{settings.IgUserId}/media_publish", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"PublishContainer failed ({resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var mediaId = doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Publish response missing id");

        logger.LogInformation("Published container {Container} → IG Media {MediaId}", containerId, mediaId);
        return mediaId;
    }

    // ── Comments ─────────────────────────────────────────────────────

    /// <summary>
    /// Posts a comment on a published IG media object.
    /// Used to pin the episode link list as the first comment on a carousel/single post.
    /// Returns the new comment ID.
    /// </summary>
    public async Task<string> PostCommentAsync(
        string mediaId, string text, CancellationToken ct = default)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["message"]      = text,
            ["access_token"] = settings.AccessToken
        });

        var resp = await Http.PostAsync($"{BaseUrl}/{mediaId}/comments", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"PostComment failed ({resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Comment response missing id");
    }

    // ── Token management ─────────────────────────────────────────────

    /// <summary>
    /// Re-extends the long-lived Facebook User Access Token for another 60 days.
    /// Requires AppId and AppSecret in settings.
    /// Run monthly; update INSTAGRAM_ACCESS_TOKEN secret with the returned token.
    /// </summary>
    public async Task<(string NewToken, long ExpiresInSeconds)> RefreshTokenAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.AppId) || string.IsNullOrWhiteSpace(settings.AppSecret))
            throw new InvalidOperationException("AppId and AppSecret are required to refresh the token");

        var url = $"https://graph.facebook.com/oauth/access_token"
                + $"?grant_type=fb_exchange_token"
                + $"&client_id={Uri.EscapeDataString(settings.AppId)}"
                + $"&client_secret={Uri.EscapeDataString(settings.AppSecret)}"
                + $"&fb_exchange_token={Uri.EscapeDataString(settings.AccessToken)}";

        var resp = await Http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token refresh failed ({resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return (
            doc.RootElement.GetProperty("access_token").GetString()!,
            doc.RootElement.GetProperty("expires_in").GetInt64()
        );
    }

    /// <summary>Returns days remaining until the token expires (-1 if check fails).</summary>
    public async Task<double> GetTokenExpiryDaysAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"https://graph.facebook.com/debug_token"
                    + $"?input_token={Uri.EscapeDataString(settings.AccessToken)}"
                    + $"&access_token={Uri.EscapeDataString(settings.AccessToken)}";

            var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return double.MaxValue;

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("expires_at", out var expiresAt))
            {
                var expiresAtValue = expiresAt.GetInt64();
                // System User tokens never expire — Meta returns expires_at=0 for them
                if (expiresAtValue == 0) return double.MaxValue;
                var secs = expiresAtValue - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return secs / 86400.0;
            }
            return double.MaxValue;
        }
        catch { return double.MaxValue; }
    }
}
