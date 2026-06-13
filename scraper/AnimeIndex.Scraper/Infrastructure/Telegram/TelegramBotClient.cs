using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.Telegram;

/// <summary>
/// Thin wrapper around the Telegram Bot API.
/// Uses sendPhoto when a cover image URL is available, sendMessage as fallback.
/// Docs: https://core.telegram.org/bots/api
/// </summary>
public class TelegramBotClient(
    IHttpClientFactory httpFactory,
    TelegramSettings settings,
    ILogger<TelegramBotClient> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private string ApiBase => $"https://api.telegram.org/bot{settings.BotToken}";

    /// <summary>
    /// Sends a photo message with caption to the channel.
    /// Falls back to text-only message if no photo URL is provided.
    /// Returns the Telegram message_id on success, null on failure.
    /// </summary>
    public async Task<string?> SendEpisodeAsync(
        string caption,
        string? photoUrl,
        CancellationToken ct = default)
    {
        if (!settings.IsConfigured)
        {
            logger.LogDebug("Telegram not configured — skipping send");
            return null;
        }

        var client = httpFactory.CreateClient("telegram");

        try
        {
            string endpoint;
            object payload;

            if (!string.IsNullOrWhiteSpace(photoUrl))
            {
                endpoint = $"{ApiBase}/sendPhoto";
                payload = new
                {
                    chat_id = settings.ChannelId,
                    photo = photoUrl,
                    caption,
                    parse_mode = "HTML",
                };
            }
            else
            {
                endpoint = $"{ApiBase}/sendMessage";
                payload = new
                {
                    chat_id = settings.ChannelId,
                    text = caption,
                    parse_mode = "HTML",
                };
            }

            var json = JsonSerializer.Serialize(payload, JsonOpts);

            // Telegram limita ~20 msgs/min por canal: en rachas largas devuelve
            // 429 con parameters.retry_after. Esperamos lo que pida y reintentamos
            // (hasta 3 veces) en vez de perder el post.
            const int MaxAttempts = 3;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await client.PostAsync(endpoint, content, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < MaxAttempts)
                {
                    var retryAfter = 5;
                    try
                    {
                        using var errDoc = JsonDocument.Parse(body);
                        if (errDoc.RootElement.TryGetProperty("parameters", out var p) &&
                            p.TryGetProperty("retry_after", out var ra))
                            retryAfter = Math.Clamp(ra.GetInt32(), 1, 60);
                    }
                    catch { /* body sin parameters — usar default */ }

                    logger.LogInformation(
                        "Telegram 429 — esperando {Seconds}s y reintentando ({Attempt}/{Max})",
                        retryAfter, attempt, MaxAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(retryAfter + 1), ct);
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    logger.LogWarning("Telegram API returned {Status}: {Body}", (int)resp.StatusCode, body);
                    return null;
                }

                using var doc = JsonDocument.Parse(body);
                return doc.RootElement
                    .GetProperty("result")
                    .GetProperty("message_id")
                    .GetInt64()
                    .ToString();
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telegram API call failed");
            return null;
        }
    }
}
