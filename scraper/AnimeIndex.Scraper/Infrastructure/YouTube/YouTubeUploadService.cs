using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>Resultado de una subida: el video siempre; miniatura y playlist son best-effort.</summary>
public sealed record YouTubeUploadResult(string VideoId, string PrivacyStatus, bool ThumbnailSet, string? Playlist)
{
    public string Url => $"https://youtu.be/{VideoId}";
}

/// <summary>
/// Sube el video renderizado al canal con la YouTube Data API v3:
///   1. refresh token → access token (oauth2.googleapis.com/token),
///   2. videos.insert resumable (~1.600 unidades de las 10.000 diarias),
///   3. thumbnails.set (50 u.; solo anda con el canal verificado por teléfono),
///   4. playlistItems.insert en la playlist buscada por título (1 + 50 u.).
/// 3 y 4 son best-effort: si fallan se loguea y el video queda subido igual.
/// Mientras el proyecto de Google Cloud no pase la auditoría de YouTube API
/// Services, YouTube fuerza privado todo lo que sube la API.
/// La PANTALLA FINAL no se puede cargar por API (la v3 no tiene recurso de end
/// screens ni de tarjetas): el video deja los últimos 15 s limpios
/// (<see cref="VideoRenderService.EndScreenSeconds"/>) y los elementos se
/// agregan a mano en Studio → Editor → Pantalla final → "Importar del video".
/// </summary>
public sealed class YouTubeUploadService(
    IHttpClientFactory httpFactory, YouTubeSettings settings, ILogger<YouTubeUploadService> logger)
{
    public const string HttpClientName = "youtube-upload";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string ApiBase = "https://www.googleapis.com/youtube/v3";
    private const string UploadBase = "https://www.googleapis.com/upload/youtube/v3";
    /// <summary>Límite de thumbnails.set.</summary>
    private const long MaxThumbnailBytes = 2 * 1024 * 1024;
    private const int MaxUploadAttempts = 5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<YouTubeUploadResult> UploadAsync(
        string videoPath, string? thumbnailPath, YouTubeVideoResource resource, string? playlist, CancellationToken ct)
    {
        if (!settings.IsConfigured)
            throw new InvalidOperationException("Faltan YouTube__ClientId, YouTube__ClientSecret o YouTube__RefreshToken");
        if (!File.Exists(videoPath))
            throw new FileNotFoundException("No está el video a subir", videoPath);

        var http = httpFactory.CreateClient(HttpClientName);
        var token = await GetAccessTokenAsync(http, ct);

        var videoId = await InsertVideoAsync(http, token, videoPath, resource, ct);
        logger.LogInformation("Subido: https://youtu.be/{Id} ({Privacy})", videoId, resource.Status.PrivacyStatus);
        logger.LogInformation("Falta la pantalla final (no hay API): Studio → Editor → Pantalla final, sobre los últimos {S:F0} s",
            VideoRenderService.EndScreenSeconds);

        var thumb = thumbnailPath is not null && await TrySetThumbnailAsync(http, token, videoId, thumbnailPath, ct);
        var added = playlist is not null ? await TryAddToPlaylistAsync(http, token, videoId, playlist, ct) : null;
        return new YouTubeUploadResult(videoId, resource.Status.PrivacyStatus, thumb, added);
    }

    private async Task<string> GetAccessTokenAsync(HttpClient http, CancellationToken ct)
    {
        using var res = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["client_secret"] = settings.ClientSecret,
            ["refresh_token"] = settings.RefreshToken,
            ["grant_type"] = "refresh_token",
        }), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            // El cuerpo de error de Google no trae secretos: {"error":"invalid_grant","error_description":"..."}.
            var hint = body.Contains("invalid_grant")
                ? " — se revocó el acceso o cambió la contraseña de la cuenta: regenerar el refresh token (youtube-output/canal/setup_youtube_oauth.py)"
                : "";
            throw new InvalidOperationException($"OAuth de YouTube: HTTP {(int)res.StatusCode} {ErrorOf(body)}{hint}");
        }
        return JsonNode.Parse(body)?["access_token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("OAuth de YouTube: respuesta sin access_token");
    }

    private async Task<string> InsertVideoAsync(
        HttpClient http, string token, string videoPath, YouTubeVideoResource resource, CancellationToken ct)
    {
        var length = new FileInfo(videoPath).Length;

        using var init = new HttpRequestMessage(HttpMethod.Post,
            $"{UploadBase}/videos?uploadType=resumable&part=snippet,status&notifySubscribers=false")
        {
            Content = JsonContent.Create(resource, options: Json),
        };
        init.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        init.Headers.Add("X-Upload-Content-Type", "video/mp4");
        init.Headers.Add("X-Upload-Content-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var initRes = await http.SendAsync(init, ct);
        if (!initRes.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"videos.insert (inicio): HTTP {(int)initRes.StatusCode} {ErrorOf(await initRes.Content.ReadAsStringAsync(ct))}");
        var session = initRes.Headers.Location
            ?? throw new InvalidOperationException("videos.insert: sin URL de sesión resumable");

        logger.LogInformation("Subiendo {Mb:0.0} MB…", length / 1048576.0);
        long offset = 0;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var file = File.OpenRead(videoPath);
                file.Seek(offset, SeekOrigin.Begin);
                using var put = new HttpRequestMessage(HttpMethod.Put, session) { Content = new StreamContent(file) };
                put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                put.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                put.Content.Headers.ContentLength = length - offset;
                if (offset > 0) put.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, length - 1, length);

                using var res = await http.SendAsync(put, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                if (res.IsSuccessStatusCode)
                    return JsonNode.Parse(body)?["id"]?.GetValue<string>()
                        ?? throw new InvalidOperationException("videos.insert: respuesta sin id");
                if ((int)res.StatusCode < 500 || attempt >= MaxUploadAttempts)
                    throw new InvalidOperationException($"videos.insert: HTTP {(int)res.StatusCode} {ErrorOf(body)}");
                logger.LogWarning("videos.insert: HTTP {Status}, reintento {Attempt}", (int)res.StatusCode, attempt);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt >= MaxUploadAttempts) throw;
                logger.LogWarning("videos.insert se cortó ({Msg}), reintento {Attempt}", ex.Message, attempt);
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            offset = await QueryOffsetAsync(http, token, session, length, ct);
        }
    }

    /// <summary>Cuánto recibió YouTube de una subida cortada: PUT vacío con "Content-Range: bytes */total" → 308 + Range.</summary>
    private static async Task<long> QueryOffsetAsync(HttpClient http, string token, Uri session, long length, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent([]) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content.Headers.ContentRange = new ContentRangeHeaderValue(length);
        using var res = await http.SendAsync(req, ct);
        if ((int)res.StatusCode == 308 && res.Headers.TryGetValues("Range", out var ranges))
        {
            var last = ranges.First().Split('-').LastOrDefault();
            if (long.TryParse(last, out var end)) return end + 1;
        }
        return 0;
    }

    private async Task<bool> TrySetThumbnailAsync(HttpClient http, string token, string videoId, string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (new FileInfo(path).Length > MaxThumbnailBytes)
            {
                logger.LogWarning("Miniatura de más de 2 MB: queda la automática de YouTube");
                return false;
            }
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{UploadBase}/thumbnails/set?videoId={videoId}")
            {
                Content = new ByteArrayContent(await File.ReadAllBytesAsync(path, ct)),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            using var res = await http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode) return true;
            // 403 forbidden mientras el canal no esté verificado por teléfono (youtube.com/verify).
            logger.LogWarning("thumbnails.set: HTTP {Status} {Error} — ¿canal sin verificar? Queda la miniatura automática",
                (int)res.StatusCode, ErrorOf(await res.Content.ReadAsStringAsync(ct)));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("thumbnails.set falló: {Msg}", ex.Message);
        }
        return false;
    }

    private async Task<string?> TryAddToPlaylistAsync(HttpClient http, string token, string videoId, string wanted, CancellationToken ct)
    {
        try
        {
            using var list = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/playlists?part=snippet&mine=true&maxResults=50");
            list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var listRes = await http.SendAsync(list, ct);
            var listBody = await listRes.Content.ReadAsStringAsync(ct);
            if (!listRes.IsSuccessStatusCode)
            {
                logger.LogWarning("playlists.list: HTTP {Status} {Error}", (int)listRes.StatusCode, ErrorOf(listBody));
                return null;
            }
            var match = JsonNode.Parse(listBody)?["items"]?.AsArray()
                .Select(i => (Id: i?["id"]?.GetValue<string>(), Title: i?["snippet"]?["title"]?.GetValue<string>() ?? ""))
                .FirstOrDefault(p => p.Id is not null && YouTubeMetadata.PlaylistMatches(p.Title, wanted));
            if (match?.Id is null)
            {
                logger.LogWarning("No hay una playlist \"{Wanted}\" en el canal: el video queda sin playlist", wanted);
                return null;
            }

            using var add = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/playlistItems?part=snippet")
            {
                Content = JsonContent.Create(new
                {
                    snippet = new { playlistId = match.Value.Id, resourceId = new { kind = "youtube#video", videoId } },
                }),
            };
            add.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var addRes = await http.SendAsync(add, ct);
            if (addRes.IsSuccessStatusCode)
            {
                logger.LogInformation("Agregado a la playlist \"{Title}\"", match.Value.Title);
                return match.Value.Title;
            }
            logger.LogWarning("playlistItems.insert: HTTP {Status} {Error}",
                (int)addRes.StatusCode, ErrorOf(await addRes.Content.ReadAsStringAsync(ct)));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Playlist falló: {Msg}", ex.Message);
        }
        return null;
    }

    /// <summary>Lo útil de un error de Google: "reason: message" (o el cuerpo recortado).</summary>
    private static string ErrorOf(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node?["error"] is JsonObject err)
            {
                var reason = err["errors"]?.AsArray().FirstOrDefault()?["reason"]?.GetValue<string>();
                var message = err["message"]?.GetValue<string>();
                return string.Join(": ", new[] { reason, message }.Where(s => !string.IsNullOrEmpty(s)));
            }
            if (node?["error"] is JsonValue code)
                return code.ToString() + (node["error_description"] is { } d ? $": {d}" : "");
        }
        catch (JsonException) { }
        return body.Length > 300 ? body[..300] : body;
    }
}
