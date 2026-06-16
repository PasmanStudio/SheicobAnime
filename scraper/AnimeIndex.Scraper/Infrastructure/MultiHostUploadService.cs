using System.Net.Http.Headers;
using System.Text.Json;
using AnimeIndex.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

// MirrorScrapedData vive en el namespace top-level de AnimeIndex.Scraper.
using AnimeIndex.Scraper;

namespace AnimeIndex.Scraper.Infrastructure;

/// <summary>
/// Replica el video de un episodio a hosts PROPIOS adicionales con reparto de
/// ingresos (DoodStream, Voe). Cada host embebido corre SUS anuncios y nos paga
/// por vista.
///
/// IMPORTANTE — usa LOCAL upload, no remote-upload-por-URL. El remote upload falla
/// porque las URLs que resolvemos (streamtape, etc.) son IP-bound al runner de GHA:
/// cuando el host las descarga desde SU IP, el origen rechaza el token (0 bytes,
/// error). En cambio acá el scraper DESCARGA el MP4 él mismo (misma IP que lo
/// resolvió → token válido) y SUBE el archivo por POST. Misma lógica que SeekStreaming.
///
/// Flujo de cada host (patrón XFileSharing):
///   1. GET  {apiBase}/api/upload/server?key=KEY → URL del nodo de subida
///   2. POST multipart (key + file) al nodo       → filecode
///   3. embed = {embedBase}/e/{filecode}          → se upserta como mirror
///
/// Diseño:
///  - Best-effort: nunca lanza. Un fallo no afecta a SeekStreaming ni al scraping.
///  - No-op si la API key del host no está configurada.
///  - Descarga UNA sola vez y sube a todos los hosts pendientes desde ese temp file.
///  - Dedup: si el episodio ya tiene mirror activo de ese provider, no re-sube.
///
/// Config (env entre [corchetes]):
///   Doodstream:ApiKey   [DOODSTREAM__APIKEY]   — sin esto, DoodStream se omite (XFileSharing)
///   Doodstream:ApiBase  [DOODSTREAM__APIBASE]  — default https://doodapi.co
///   Doodstream:EmbedBase[DOODSTREAM__EMBEDBASE]— default https://dood.wf
///   Player4me:ApiKey    [PLAYER4ME__APIKEY]    — sin esto, player4me se omite (TUS)
///   Player4me:ApiBase   [PLAYER4ME__APIBASE]   — default https://player4me.com (dashboard/API)
///   Player4me:EmbedBase [PLAYER4ME__EMBEDBASE] — default https://player4me.online (player real; embed = {base}/#{id})
/// </summary>
public sealed class MultiHostUploadService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly UpsertPipelineService _upsert;
    private readonly AppDbContext _db;
    private readonly TusVideoUploader _tus;
    private readonly ILogger<MultiHostUploadService> _logger;
    private readonly IReadOnlyList<HostConfig> _hosts;

    /// <summary>Mecanismo de subida del host.</summary>
    private enum HostKind
    {
        /// XFileSharing: GET /api/upload/server → POST file. Auth por ?key=. (DoodStream)
        Xfs,
        /// Seek-compatible TUS: GET /api/v1/video/upload → tus. Auth por header api-token. (player4me)
        Tus,
    }

    /// <param name="Provider">Provider name PROPIO (distinto de los externos de jkanime).</param>
    private sealed record HostConfig(HostKind Kind, string Provider, string ApiKey, string ApiBase, string EmbedBase);

    public MultiHostUploadService(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        UpsertPipelineService upsert,
        AppDbContext db,
        TusVideoUploader tus,
        ILogger<MultiHostUploadService> logger)
    {
        _httpFactory = httpFactory;
        _upsert = upsert;
        _db = db;
        _tus = tus;
        _logger = logger;

        var hosts = new List<HostConfig>();

        var doodKey = config["Doodstream:ApiKey"];
        if (!string.IsNullOrWhiteSpace(doodKey))
            hosts.Add(new HostConfig(
                HostKind.Xfs,
                "doodstream",
                doodKey,
                config["Doodstream:ApiBase"]?.TrimEnd('/') ?? "https://doodapi.co",
                config["Doodstream:EmbedBase"]?.TrimEnd('/') ?? "https://dood.wf"));

        var p4mKey = config["Player4me:ApiKey"];
        if (!string.IsNullOrWhiteSpace(p4mKey))
            hosts.Add(new HostConfig(
                HostKind.Tus,
                "player4me",
                p4mKey,
                config["Player4me:ApiBase"]?.TrimEnd('/') ?? "https://player4me.com",
                config["Player4me:EmbedBase"]?.TrimEnd('/') ?? "https://player4me.online"));

        _hosts = hosts;
    }

    /// <summary>true si hay al menos un host configurado (hay API key).</summary>
    public bool Enabled => _hosts.Count > 0;

    /// <summary>
    /// Descarga el MP4 una sola vez y lo sube por POST a cada host propio pendiente,
    /// registrando el embed como mirror. Best-effort: nunca lanza.
    /// </summary>
    public async Task ReplicateAsync(Guid episodeId, string directUrl, string? referer, CancellationToken ct = default)
    {
        if (_hosts.Count == 0 || string.IsNullOrWhiteSpace(directUrl)) return;

        // Dedup primero: si ya están todos, ni descargamos.
        var pending = new List<HostConfig>();
        foreach (var h in _hosts)
        {
            var already = await _db.Mirrors
                .AsNoTracking()
                .AnyAsync(m => m.EpisodeId == episodeId && m.ProviderName == h.Provider && m.IsActive, ct);
            if (!already) pending.Add(h);
        }
        if (pending.Count == 0) return;

        // Nombre con sentido: "{slug}-{nº}.mp4" en vez de "video.mp4", para que en
        // los paneles de DoodStream/Voe cada archivo referencie su episodio.
        var fileName = await BuildFileNameAsync(episodeId, ct);

        var tempPath = await DownloadToTempAsync(directUrl, referer, episodeId, ct);
        if (tempPath is null)
        {
            _logger.LogDebug("MultiHost: no se pudo descargar el MP4 (ep={EpisodeId}), omito replicación", episodeId);
            return;
        }

        try
        {
            foreach (var host in pending)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    if (host.Kind == HostKind.Tus)
                        await TusUploadToHostAsync(host, episodeId, tempPath, fileName, ct);
                    else
                        await LocalUploadToHostAsync(host, episodeId, tempPath, fileName, ct);
                }
                catch (TaskCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "MultiHost: {Provider} falló (ep={EpisodeId})", host.Provider, episodeId);
                }
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Arma "{slug}-{nº}.mp4" desde el episodio. Los slugs ya son URL-safe → válidos
    /// como nombre de archivo. Fallback "video.mp4" si el episodio no se encuentra.
    /// </summary>
    private async Task<string> BuildFileNameAsync(Guid episodeId, CancellationToken ct)
    {
        try
        {
            var info = await _db.Episodes
                .AsNoTracking()
                .Where(e => e.Id == episodeId)
                .Select(e => new { e.EpisodeNumber, Slug = e.Series.Slug })
                .FirstOrDefaultAsync(ct);

            if (info is not null && !string.IsNullOrWhiteSpace(info.Slug))
                return $"{info.Slug}-{info.EpisodeNumber}.mp4";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "MultiHost: no se pudo armar el nombre (ep={EpisodeId})", episodeId);
        }
        return "video.mp4";
    }

    /// <summary>Descarga directUrl a un temp file (misma IP que lo resolvió → token válido).</summary>
    private async Task<string?> DownloadToTempAsync(string directUrl, string? referer, Guid episodeId, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mh_{Guid.NewGuid():N}.mp4");
        try
        {
            // "seek-download" ya trae timeout de 90 min + User-Agent de browser.
            var dl = _httpFactory.CreateClient("seek-download");
            using var req = new HttpRequestMessage(HttpMethod.Get, directUrl);
            if (!string.IsNullOrEmpty(referer))
                req.Headers.TryAddWithoutValidation("Referer", referer);

            using var resp = await dl.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(path))
            {
                await src.CopyToAsync(dst, 1 << 20, ct);
            }

            var size = new FileInfo(path).Length;
            if (size <= 0)
            {
                TryDelete(path);
                return null;
            }

            _logger.LogDebug("MultiHost: descargado {Size} bytes a temp (ep={EpisodeId})", size, episodeId);
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "MultiHost: descarga del MP4 falló (ep={EpisodeId})", episodeId);
            TryDelete(path);
            return null;
        }
    }

    /// <summary>
    /// Subida TUS (player4me / Seek-compatible): sube el archivo y registra el embed.
    /// OJO — estos hosts NO embeben por <c>/e/{id}</c> (eso es la SPA del dashboard,
    /// que devuelve 404). El reproductor real vive en el dominio de player y lee el
    /// id del fragmento hash: <c>{embedBase}/#{id}</c> (igual que SeekStreaming, que
    /// usa <c>https://sheicobanime.seekplayer.me/#{id}</c>).
    /// </summary>
    private async Task TusUploadToHostAsync(HostConfig host, Guid episodeId, string filePath, string fileName, CancellationToken ct)
    {
        var id = await _tus.UploadFileAsync(host.ApiBase, host.ApiKey, filePath, fileName, pollTimeoutMinutes: 12, ct);
        if (string.IsNullOrEmpty(id))
        {
            _logger.LogDebug("MultiHost: {Provider} sin id tras TUS (ep={EpisodeId})", host.Provider, episodeId);
            return;
        }

        var embedUrl = $"{host.EmbedBase}/#{id}";
        await _upsert.UpsertMirrorAsync(new MirrorScrapedData(
            EpisodeId: episodeId,
            ProviderName: host.Provider,
            EmbedUrl: embedUrl,
            QualityLabel: 720,
            Priority: 1), ct);

        _logger.LogInformation(
            "✅ MultiHost: ep={EpisodeId} subido a {Provider} (id={Id}, embed={Embed})",
            episodeId, host.Provider, id, embedUrl);
    }

    private async Task LocalUploadToHostAsync(HostConfig host, Guid episodeId, string filePath, string fileName, CancellationToken ct)
    {
        var api = _httpFactory.CreateClient("multihost"); // timeout largo: el POST sube el archivo entero

        // 1. Nodo de subida
        using var serverResp = await api.GetAsync(
            $"{host.ApiBase}/api/upload/server?key={Uri.EscapeDataString(host.ApiKey)}", ct);
        if (!serverResp.IsSuccessStatusCode)
        {
            _logger.LogDebug("MultiHost: {Provider} upload/server HTTP {Status} (ep={EpisodeId})",
                host.Provider, (int)serverResp.StatusCode, episodeId);
            return;
        }
        var serverUrl = ReadServerUrl(await serverResp.Content.ReadAsStringAsync(ct));
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            _logger.LogDebug("MultiHost: {Provider} sin URL de nodo (ep={EpisodeId})", host.Provider, episodeId);
            return;
        }

        // 2. POST del archivo (key como form field y en la query — cubre ambas convenciones)
        using var form = new MultipartFormDataContent
        {
            { new StringContent(host.ApiKey), "api_key" },
            { new StringContent(host.ApiKey), "key" },
        };
        await using var fs = File.OpenRead(filePath);
        var fileContent = new StreamContent(fs);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        form.Add(fileContent, "file", fileName);

        var postUrl = serverUrl + (serverUrl.Contains('?') ? "&" : "?") + "key=" + Uri.EscapeDataString(host.ApiKey);
        using var uploadResp = await api.PostAsync(postUrl, form, ct);
        if (!uploadResp.IsSuccessStatusCode)
        {
            _logger.LogDebug("MultiHost: {Provider} POST file HTTP {Status} (ep={EpisodeId})",
                host.Provider, (int)uploadResp.StatusCode, episodeId);
            return;
        }

        var body = await uploadResp.Content.ReadAsStringAsync(ct);
        var filecode = ParseFilecode(body);
        if (string.IsNullOrEmpty(filecode))
        {
            _logger.LogDebug("MultiHost: {Provider} sin filecode en la respuesta (ep={EpisodeId}): {Body}",
                host.Provider, episodeId, body.Length > 200 ? body[..200] : body);
            return;
        }

        var embedUrl = $"{host.EmbedBase}/e/{filecode}";

        // Priority 1: debajo de SeekStreaming (0), encima de los externos (50).
        await _upsert.UpsertMirrorAsync(new MirrorScrapedData(
            EpisodeId: episodeId,
            ProviderName: host.Provider,
            EmbedUrl: embedUrl,
            QualityLabel: 720,
            Priority: 1), ct);

        _logger.LogInformation(
            "✅ MultiHost: ep={EpisodeId} subido a {Provider} (filecode={Filecode}, embed={Embed})",
            episodeId, host.Provider, filecode, embedUrl);
    }

    /// <summary>Lee la URL del nodo de subida de {"result":"https://..."}.</summary>
    private static string? ReadServerUrl(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("result", out var r) &&
                r.ValueKind == JsonValueKind.String)
                return r.GetString();
        }
        catch (JsonException) { /* no-JSON */ }
        return null;
    }

    /// <summary>
    /// Busca recursivamente el primer "filecode"/"file_code" en la respuesta —
    /// tolerante a DoodStream ({"result":[{"filecode":...}]}) y Voe ({"file_code":...}).
    /// </summary>
    private static string? ParseFilecode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FindFilecode(doc.RootElement);
        }
        catch (JsonException) { return null; }
    }

    private static string? FindFilecode(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                    if ((p.NameEquals("filecode") || p.NameEquals("file_code")) &&
                        p.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = p.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                foreach (var p in el.EnumerateObject())
                {
                    var r = FindFilecode(p.Value);
                    if (r is not null) return r;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    var r = FindFilecode(item);
                    if (r is not null) return r;
                }
                return null;
            default:
                return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }
}
