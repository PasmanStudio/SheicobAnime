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

    // Todo lo que ESTA corrida subió a Cloudinary, para poder borrarlo al final
    // (ver PurgeUploadedAsync). Solo se registra acá lo que sube este cliente:
    // la biblioteca de música de ReelMusicService ("{CloudinaryFolder}/music")
    // sube por otro camino y por eso NUNCA entra en esta lista.
    private readonly List<(string PublicId, ResourceType Type)> _uploaded = [];

    /// <summary>
    /// Borra de Cloudinary todo lo que subió esta corrida. Se llama al terminar,
    /// haya salido bien o mal: una vez que Meta procesó el container, copia el
    /// archivo a su propio CDN y la URL deja de hacer falta; y si la publicación
    /// falló, el asset no sirve para nada (no hay reintento entre corridas).
    ///
    /// Sin esto no se borraba NADA nunca: ~130 MB por día acumulándose (6,95 GB
    /// al 6-sep-2026, el 59% del consumo de créditos del plan free).
    ///
    /// Best-effort a propósito: un fallo borrando no puede tirar una corrida que
    /// ya publicó — a lo sumo queda un huérfano que la próxima purga se lleva.
    /// </summary>
    public async Task PurgeUploadedAsync(CancellationToken ct = default)
    {
        if (!settings.CloudinaryPurgeAfterRun || _uploaded.Count == 0) return;

        var borrados = 0;
        foreach (var (publicId, type) in _uploaded)
        {
            try
            {
                var result = await Cloudinary.DestroyAsync(new DeletionParams(publicId) { ResourceType = type });
                if (string.Equals(result.Result, "ok", StringComparison.OrdinalIgnoreCase)) borrados++;
                else logger.LogDebug("Cloudinary destroy {Id}: {Result}", publicId, result.Result);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "No se pudo borrar {Id} de Cloudinary", publicId);
            }
        }

        logger.LogInformation("Cloudinary: {Deleted}/{Total} asset(s) de la corrida borrados",
            borrados, _uploaded.Count);
        _uploaded.Clear();
    }

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

        if (result.PublicId is { Length: > 0 } pid)
            _uploaded.Add((pid, ResourceType.Image));

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

        if (result.PublicId is { Length: > 0 } pid)
            _uploaded.Add((pid, ResourceType.Video));

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
        string videoUrl, string? caption, bool shareToFeed = true,
        string? coverUrl = null, CancellationToken ct = default)
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
        // Sin cover, IG usa el primer frame — que en nuestros reels es NEGRO
        // (el video abre con fade-in desde negro) → la miniatura del feed salía
        // negra. El cover es la tarjeta 9:16 (JPEG ≤8MB según specs).
        if (!string.IsNullOrWhiteSpace(coverUrl))
            fields["cover_url"] = coverUrl;

        return await CreateContainerAsync("CreateReel", fields, ct);
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

        return await CreateContainerAsync("CreateVideoStory", fields, ct);
    }

    // ── Carousel workflow ─────────────────────────────────────────────

    /// <summary>
    /// Creates a carousel child (item) container for one image.
    /// Must be called for each image before creating the carousel container.
    /// </summary>
    public Task<string> CreateCarouselItemContainerAsync(
        string imageUrl, CancellationToken ct = default)
        => CreateContainerAsync("CreateCarouselItem", new Dictionary<string, string>
        {
            ["image_url"]        = imageUrl,
            ["is_carousel_item"] = "true",
            ["access_token"]     = settings.AccessToken
        }, ct);

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

        var id = await CreateContainerAsync("CreateCarouselContainer", fields, ct);

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

        return await CreateContainerAsync("CreateSingleImage", fields, ct);
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

        return await CreateContainerAsync("CreateStory", fields, ct);
    }

    // ── Reintentos ante fallos transitorios de Meta ───────────────────

    /// <summary>
    /// Subcódigo de Meta para "no pude descargar el contenido multimedia de esa
    /// URI". Meta lo marca <c>is_transient:false</c> pero NO lo es: el 6-sep-2026
    /// tiró 2 de 4 reels y 3 de 3 carruseles mientras las MISMAS imágenes seguían
    /// sirviéndose horas después (HTTP 200, image/jpeg, 1080x1080, ~180 KB —
    /// dentro de todos los límites de Instagram). Los assets estaban bien; el
    /// fetcher de Meta parpadeó y el código no reintentaba ni una vez.
    /// </summary>
    private const int MediaDownloadFailureSubcode = 2207052;

    private const int PublishAttempts = 3;

    /// <summary>
    /// ¿Vale la pena reintentar esta respuesta de error? Los 5xx y el
    /// <see cref="MediaDownloadFailureSubcode"/> sí; un token vencido o un
    /// caption inválido no — reintentar eso solo quema tiempo.
    /// Público estático para tests.
    /// </summary>
    public static bool IsTransientPublishError(System.Net.HttpStatusCode status, string body)
    {
        if ((int)status >= 500) return true;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var err)) return false;

            if (err.TryGetProperty("error_subcode", out var sub)
                && sub.TryGetInt32(out var subcode)
                && subcode == MediaDownloadFailureSubcode)
                return true;

            // Meta marca así sus fallos internos declarados; se respeta cuando viene
            return err.TryGetProperty("is_transient", out var tr)
                && tr.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// POST a /media reintentando ante fallos transitorios. Devuelve el id del
    /// container. Lo usan TODOS los tipos de container (carrusel, reel, story):
    /// el parpadeo del fetcher de Meta no distingue formato.
    /// </summary>
    private async Task<string> CreateContainerAsync(
        string what, Dictionary<string, string> fields, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var form = new FormUrlEncodedContent(fields);
            var resp = await Http.PostAsync($"{BaseUrl}/{settings.IgUserId}/media", form, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.GetProperty("id").GetString()
                    ?? throw new InvalidOperationException($"{what} response missing id");
            }

            if (attempt >= PublishAttempts || !IsTransientPublishError(resp.StatusCode, body))
                throw new InvalidOperationException($"{what} failed ({resp.StatusCode}): {body}");

            // Backoff generoso: lo que falla es que Meta vaya a buscar el archivo
            // a Cloudinary, y reintentar al toque suele comerse el mismo parpadeo.
            var wait = TimeSpan.FromSeconds(5 * attempt);
            logger.LogWarning(
                "{What}: fallo transitorio de Meta (intento {Attempt}/{Total}) — reintento en {Wait}s. {Body}",
                what, attempt, PublishAttempts, wait.TotalSeconds, Truncate(body, 300));
            await Task.Delay(wait, ct);
        }
    }

    /// <summary>
    /// Crea el container, espera a que Meta lo procese y lo publica, reintentando
    /// la SECUENCIA COMPLETA ante un <c>status_code=ERROR</c>. Hace falta el ciclo
    /// entero: un container que terminó en ERROR queda quemado, así que reintentar
    /// solo el publish no sirve — hay que crear uno nuevo apuntando a la misma URL
    /// (que sigue viva en Cloudinary, no se resube nada).
    /// </summary>
    public async Task<string> CreateWaitPublishAsync(
        Func<CancellationToken, Task<string>> createContainer,
        TimeSpan? processingTimeout = null,
        CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var containerId = await createContainer(ct);
            try
            {
                await WaitForContainerReadyAsync(containerId, ct, processingTimeout);
                return await PublishContainerAsync(containerId, ct);
            }
            catch (ContainerProcessingException ex) when (attempt < PublishAttempts)
            {
                var wait = TimeSpan.FromSeconds(5 * attempt);
                logger.LogWarning(
                    "Container {Id} terminó en {Status} (intento {Attempt}/{Total}) — se recrea en {Wait}s",
                    containerId, ex.Status, attempt, PublishAttempts, wait.TotalSeconds);
                await Task.Delay(wait, ct);
            }
        }
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
                    // Excepción propia para que CreateWaitPublishAsync pueda
                    // distinguir "Meta no pudo procesar este container" (recreable)
                    // de cualquier otro fallo.
                    throw new ContainerProcessingException(containerId, status ?? "UNKNOWN");
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

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// Meta terminó de procesar el container y lo dejó en ERROR/EXPIRED. Es un
/// estado del CONTAINER, no del contenido: el mismo video/imagen suele entrar
/// bien en un container nuevo, así que <see cref="MetaGraphApiClient.CreateWaitPublishAsync"/>
/// lo trata como reintentable.
/// </summary>
public sealed class ContainerProcessingException(string containerId, string status)
    : InvalidOperationException($"Container {containerId} reached terminal status: {status}")
{
    public string ContainerId { get; } = containerId;
    public string Status { get; } = status;
}
