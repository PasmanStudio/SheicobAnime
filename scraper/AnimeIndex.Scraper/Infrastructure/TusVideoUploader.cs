using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure;

/// <summary>
/// Sube un archivo LOCAL a un host "Seek-compatible" (SeekStreaming / player4me)
/// vía TUS. Ambos exponen la misma API:
///   GET  {base}/api/v1/video/upload   → { tusUrl, accessToken }   (header api-token)
///   POST tusUrl                        → 201 + Location (slot)
///   PATCH Location en chunks de 50 MB  → 204
///   GET  {base}/api/v1/video/manage    → busca por nombre, espera status=="Active"
/// Devuelve el id del video (el embed lo arma el caller como {embedBase}/#{id} —
/// el reproductor lee el id del fragmento hash; /e/{id} es la SPA del dashboard, da 404) o null.
/// Best-effort: nunca lanza salvo cancelación.
/// </summary>
public sealed class TusVideoUploader
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<TusVideoUploader> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private const long ChunkSize = 52_428_800L; // 50 MB

    public TusVideoUploader(IHttpClientFactory httpFactory, ILogger<TusVideoUploader> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<string?> UploadFileAsync(
        string apiBase, string apiToken, string filePath, string fileName,
        int pollTimeoutMinutes, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("multihost"); // timeout 90 min

        // ── 1. credenciales tus ──
        TusCredentials? creds;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/api/v1/video/upload");
            req.Headers.TryAddWithoutValidation("api-token", apiToken);
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Tus: GET video/upload HTTP {Status} ({Base})", (int)resp.StatusCode, apiBase);
                return null;
            }
            creds = JsonSerializer.Deserialize<TusCredentials>(await resp.Content.ReadAsStringAsync(ct), JsonOpts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Tus: fallo obteniendo credenciales ({Base})", apiBase);
            return null;
        }
        if (string.IsNullOrEmpty(creds?.TusUrl) || string.IsNullOrEmpty(creds.AccessToken))
            return null;

        var size = new FileInfo(filePath).Length;

        // ── 2. crear slot tus ──
        string uploadUrl;
        try
        {
            using var create = new HttpRequestMessage(HttpMethod.Post, creds.TusUrl);
            create.Headers.TryAddWithoutValidation("Tus-Resumable", "1.0.0");
            create.Headers.TryAddWithoutValidation("Upload-Length", size.ToString());
            create.Headers.TryAddWithoutValidation("Upload-Metadata", BuildMetadata(creds.AccessToken, fileName));
            create.Content = new ByteArrayContent([]);
            create.Content.Headers.ContentLength = 0;
            using var cr = await client.SendAsync(create, ct);
            if (!cr.IsSuccessStatusCode)
            {
                _logger.LogDebug("Tus: create HTTP {Status} ({Base})", (int)cr.StatusCode, apiBase);
                return null;
            }
            uploadUrl = cr.Headers.Location?.ToString() ?? "";
            if (string.IsNullOrEmpty(uploadUrl)) return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Tus: fallo creando slot ({Base})", apiBase);
            return null;
        }

        // ── 3. patch del archivo en chunks ──
        try
        {
            await using var fs = File.OpenRead(filePath);
            var buffer = new byte[ChunkSize];
            long offset = 0;
            while (offset < size && !ct.IsCancellationRequested)
            {
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = await fs.ReadAsync(buffer.AsMemory(read), ct);
                    if (n == 0) break;
                    read += n;
                }
                if (read == 0) break;

                using var patch = new HttpRequestMessage(HttpMethod.Patch, uploadUrl);
                patch.Headers.TryAddWithoutValidation("Tus-Resumable", "1.0.0");
                patch.Headers.TryAddWithoutValidation("Upload-Offset", offset.ToString());
                patch.Content = new ByteArrayContent(buffer, 0, read);
                patch.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/offset+octet-stream");

                using var pr = await client.SendAsync(patch, ct);
                if (!pr.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Tus: PATCH HTTP {Status} en offset {Offset} ({Base})", (int)pr.StatusCode, offset, apiBase);
                    return null;
                }
                offset += read;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Tus: fallo en el PATCH ({Base})", apiBase);
            return null;
        }

        // ── 4. poll hasta Active ──
        return await PollForVideoAsync(client, apiBase, apiToken, fileName, pollTimeoutMinutes, ct);
    }

    private async Task<string?> PollForVideoAsync(
        HttpClient client, string apiBase, string apiToken, string fileName, int minutes, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(minutes);
        var noExt = Path.GetFileNameWithoutExtension(fileName);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(15_000, ct);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/api/v1/video/manage?per_page=100");
                req.Headers.TryAddWithoutValidation("api-token", apiToken);
                using var resp = await client.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;

                var list = JsonSerializer.Deserialize<VideoManageResponse>(await resp.Content.ReadAsStringAsync(ct), JsonOpts);
                var match = list?.Data?.FirstOrDefault(v =>
                    fileName.Equals(v.Name, StringComparison.OrdinalIgnoreCase) ||
                    noExt.Equals(v.Name, StringComparison.OrdinalIgnoreCase));

                if (match is null) continue;
                if (string.Equals(match.Status, "Active", StringComparison.OrdinalIgnoreCase))
                    return match.Id;
                if (string.Equals(match.Status, "Deleted", StringComparison.OrdinalIgnoreCase))
                    return null;
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tus: poll lanzó, reintento");
            }
        }
        return null;
    }

    private static string BuildMetadata(string accessToken, string filename)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        return $"accessToken {B64(accessToken)},filename {B64(filename)},filetype {B64("video/mp4")}";
    }

    private sealed record TusCredentials(
        [property: JsonPropertyName("tusUrl")] string? TusUrl,
        [property: JsonPropertyName("accessToken")] string? AccessToken);

    private sealed record VideoManageResponse(
        [property: JsonPropertyName("data")] VideoItem[]? Data);

    private sealed record VideoItem(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("status")] string? Status);
}
