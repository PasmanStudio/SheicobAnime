using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>
/// Genera un track instrumental FRESCO por reel vía sunoapi.org (wrapper de
/// terceros de Suno — no hay API oficial). Flujo: POST /api/v1/generate
/// (customMode + instrumental) → poll /api/v1/generate/record-info hasta
/// FIRST_SUCCESS/SUCCESS → descarga del audioUrl.
///
/// Best-effort por diseño: CUALQUIER problema (key inválida, sin créditos,
/// timeout, cambio de contrato del wrapper) devuelve null y el caller cae a
/// la biblioteca de Cloudinary/CC — el reel nunca se pierde por Suno.
/// </summary>
public class SunoMusicGenerator(
    IHttpClientFactory httpClientFactory,
    InstagramSettings settings,
    ILogger<SunoMusicGenerator> logger)
{
    // La generación tarda 1–3 min típicamente; margen holgado sin colgar el cron.
    private static readonly TimeSpan GenerationTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Returns the MP3 bytes of a freshly generated instrumental, or null si
    /// Suno no está configurado o algo falló (el caller decide el fallback).
    /// </summary>
    public async Task<byte[]?> GenerateInstrumentalAsync(
        string style, string title, CancellationToken ct = default)
    {
        if (!settings.SunoConfigured) return null;

        try
        {
            var http = httpClientFactory.CreateClient("suno");
            http.DefaultRequestHeaders.Authorization = new("Bearer", settings.SunoApiKey);

            // callBackUrl es obligatorio en el contrato pero usamos polling
            // (el cron es efímero, no puede recibir callbacks) — apunta a un
            // endpoint inocuo del sitio.
            var payload = new
            {
                customMode   = true,
                instrumental = true,
                style,
                title,
                model       = settings.SunoModel,
                callBackUrl = $"{settings.SiteUrl}/api/suno-callback",
            };

            using var resp = await http.PostAsJsonAsync(
                $"{settings.SunoApiUrl}/api/v1/generate", payload, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Suno generate failed ({Status}): {Body}", resp.StatusCode, Truncate(body));
                return null;
            }

            string? taskId;
            using (var doc = JsonDocument.Parse(body))
            {
                taskId = doc.RootElement.TryGetProperty("data", out var data)
                      && data.TryGetProperty("taskId", out var tid)
                    ? tid.GetString()
                    : null;
            }
            if (string.IsNullOrWhiteSpace(taskId))
            {
                logger.LogWarning("Suno generate: respuesta sin taskId: {Body}", Truncate(body));
                return null;
            }

            logger.LogInformation("Suno: generando track \"{Title}\" (style: {Style}) — task {TaskId}",
                title, Truncate(style, 80), taskId);

            var audioUrl = await PollForAudioUrlAsync(http, taskId, ct);
            if (audioUrl is null) return null;

            var mp3 = await http.GetByteArrayAsync(audioUrl, ct);
            logger.LogInformation("Suno: track \"{Title}\" listo ({Mb:F1} MB)", title, mp3.Length / 1024.0 / 1024.0);
            return mp3;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Suno generation failed — fallback a biblioteca");
            return null;
        }
    }

    private async Task<string?> PollForAudioUrlAsync(HttpClient http, string taskId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + GenerationTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, ct);

            var body = await http.GetStringAsync(
                $"{settings.SunoApiUrl}/api/v1/generate/record-info?taskId={Uri.EscapeDataString(taskId)}", ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)) continue;

            var status = data.TryGetProperty("status", out var s) ? s.GetString() : null;
            logger.LogDebug("Suno task {TaskId}: {Status}", taskId, status);

            switch (status)
            {
                // FIRST_SUCCESS = el primer track de los 2 ya está renderizado —
                // con uno alcanza, no esperamos el segundo.
                case "FIRST_SUCCESS" or "SUCCESS":
                {
                    var url = ExtractFirstAudioUrl(data);
                    if (url is not null) return url;
                    break; // SUCCESS sin URL todavía visible — seguir poll
                }
                case "CREATE_TASK_FAILED" or "GENERATE_AUDIO_FAILED"
                    or "CALLBACK_EXCEPTION" or "SENSITIVE_WORD_ERROR":
                    logger.LogWarning("Suno task {TaskId} terminó en {Status}", taskId, status);
                    return null;
            }
        }

        logger.LogWarning("Suno task {TaskId}: timeout a los {Min} min", taskId, GenerationTimeout.TotalMinutes);
        return null;
    }

    private static string? ExtractFirstAudioUrl(JsonElement data)
    {
        if (data.TryGetProperty("response", out var response)
            && response.TryGetProperty("sunoData", out var tracks)
            && tracks.ValueKind == JsonValueKind.Array)
        {
            foreach (var track in tracks.EnumerateArray())
            {
                if (track.TryGetProperty("audioUrl", out var url)
                    && url.GetString() is { Length: > 0 } audioUrl)
                    return audioUrl;
            }
        }
        return null;
    }

    private static string Truncate(string s, int max = 200) =>
        s.Length <= max ? s : s[..max] + "…";
}
