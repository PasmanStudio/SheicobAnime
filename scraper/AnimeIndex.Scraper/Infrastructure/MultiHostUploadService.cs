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
/// ingresos (DoodStream, Voe) vía su API de <c>remote upload</c> (le pasamos la
/// URL directa y ellos la descargan server-side — cero ancho de banda extra en
/// GitHub Actions). Cada host embebido corre SUS anuncios y nos paga por vista.
///
/// Diseño:
///  - Best-effort: nunca lanza. Un fallo de un host no afecta a SeekStreaming
///    ni al otro host ni al scraping.
///  - No-op si la API key del host no está configurada (mergeable sin secrets).
///  - Dedup: si el episodio ya tiene un mirror activo de ese provider, no re-sube
///    (evita duplicados cuando un episodio se reprocesa).
///
/// Se dispara una sola vez por episodio, desde SeekStreamingUploadService tras un
/// upload exitoso, reusando la MISMA URL directa ya validada.
///
/// Config (env entre [corchetes]):
///   Doodstream:ApiKey   [DOODSTREAM__APIKEY]   — sin esto, DoodStream se omite
///   Doodstream:ApiBase  [DOODSTREAM__APIBASE]  — default https://doodapi.co
///   Doodstream:EmbedBase[DOODSTREAM__EMBEDBASE]— default https://dood.wf
///   Voe:ApiKey          [VOE__APIKEY]          — sin esto, Voe se omite
///   Voe:ApiBase         [VOE__APIBASE]         — default https://voe.sx
///   Voe:EmbedBase       [VOE__EMBEDBASE]       — default https://voe.sx
/// </summary>
public sealed class MultiHostUploadService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly UpsertPipelineService _upsert;
    private readonly AppDbContext _db;
    private readonly ILogger<MultiHostUploadService> _logger;
    private readonly IReadOnlyList<HostConfig> _hosts;

    /// <param name="Provider">
    /// Provider name PROPIO. Distinto del externo de jkanime a propósito:
    /// "voe" lo usa jkanime para embeds que NO son nuestros (no nos pagan), así
    /// que nuestras subidas a Voe van como "voe-sa" para poder distinguirlas y
    /// que el filtro "solo hosts propios" no muestre embeds ajenos.
    /// </param>
    private sealed record HostConfig(string Provider, string ApiKey, string ApiBase, string EmbedBase);

    public MultiHostUploadService(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        UpsertPipelineService upsert,
        AppDbContext db,
        ILogger<MultiHostUploadService> logger)
    {
        _httpFactory = httpFactory;
        _upsert = upsert;
        _db = db;
        _logger = logger;

        var hosts = new List<HostConfig>();

        var doodKey = config["Doodstream:ApiKey"];
        if (!string.IsNullOrWhiteSpace(doodKey))
            hosts.Add(new HostConfig(
                "doodstream",
                doodKey,
                config["Doodstream:ApiBase"]?.TrimEnd('/') ?? "https://doodapi.co",
                config["Doodstream:EmbedBase"]?.TrimEnd('/') ?? "https://dood.wf"));

        var voeKey = config["Voe:ApiKey"];
        if (!string.IsNullOrWhiteSpace(voeKey))
            hosts.Add(new HostConfig(
                "voe-sa",
                voeKey,
                config["Voe:ApiBase"]?.TrimEnd('/') ?? "https://voe.sx",
                config["Voe:EmbedBase"]?.TrimEnd('/') ?? "https://voe.sx"));

        _hosts = hosts;
    }

    /// <summary>true si hay al menos un host configurado (hay API key).</summary>
    public bool Enabled => _hosts.Count > 0;

    /// <summary>
    /// Para cada host propio configurado, encola un remote upload del MP4 directo
    /// y registra el embed como mirror. Best-effort: nunca lanza.
    /// </summary>
    public async Task ReplicateAsync(Guid episodeId, string directUrl, CancellationToken ct = default)
    {
        if (_hosts.Count == 0 || string.IsNullOrWhiteSpace(directUrl)) return;

        foreach (var host in _hosts)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await ReplicateToHostAsync(host, episodeId, directUrl, ct);
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "MultiHost: {Provider} replicación falló (ep={EpisodeId})", host.Provider, episodeId);
            }
        }
    }

    private async Task ReplicateToHostAsync(HostConfig host, Guid episodeId, string directUrl, CancellationToken ct)
    {
        // Dedup: no re-subir si ya hay un mirror activo de este host para el episodio.
        var already = await _db.Mirrors
            .AsNoTracking()
            .AnyAsync(m => m.EpisodeId == episodeId && m.ProviderName == host.Provider && m.IsActive, ct);
        if (already)
        {
            _logger.LogDebug("MultiHost: {Provider} ya tiene mirror para ep={EpisodeId}, omito", host.Provider, episodeId);
            return;
        }

        var client = _httpFactory.CreateClient("multihost");
        var reqUrl =
            $"{host.ApiBase}/api/upload/url?key={Uri.EscapeDataString(host.ApiKey)}&url={Uri.EscapeDataString(directUrl)}";

        using var resp = await client.GetAsync(reqUrl, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogDebug("MultiHost: {Provider} remote-upload HTTP {Status} (ep={EpisodeId})",
                host.Provider, (int)resp.StatusCode, episodeId);
            return;
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        var filecode = ExtractFilecode(json);
        if (string.IsNullOrEmpty(filecode))
        {
            _logger.LogDebug("MultiHost: {Provider} sin filecode en la respuesta (ep={EpisodeId}): {Body}",
                host.Provider, episodeId, json.Length > 200 ? json[..200] : json);
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
            "✅ MultiHost: ep={EpisodeId} encolado en {Provider} (filecode={Filecode}, embed={Embed})",
            episodeId, host.Provider, filecode, embedUrl);
    }

    /// <summary>
    /// Extrae el filecode de la respuesta de remote upload, tolerante a las dos
    /// formas: DoodStream {"result":{"filecode":"..."}} y Voe {"file_code":"..."}.
    /// Busca filecode/file_code en result y en la raíz.
    /// </summary>
    private static string? ExtractFilecode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("result", out var result))
                {
                    var fromResult = ReadFilecode(result);
                    if (fromResult is not null) return fromResult;
                }
                var fromRoot = ReadFilecode(root);
                if (fromRoot is not null) return fromRoot;
            }
        }
        catch (JsonException)
        {
            /* respuesta no-JSON → sin filecode */
        }
        return null;
    }

    private static string? ReadFilecode(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in (ReadOnlySpan<string>)["filecode", "file_code"])
        {
            if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }
}
