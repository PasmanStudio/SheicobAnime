using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.Discord;

/// <summary>
/// Thin wrapper around Discord's Incoming Webhook API.
/// Sends rich embed messages — no bot token needed.
/// Docs: https://discord.com/developers/docs/resources/webhook#execute-webhook
/// </summary>
public class DiscordWebhookClient(
    IHttpClientFactory httpFactory,
    DiscordSettings settings,
    ILogger<DiscordWebhookClient> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Posts a webhook message with up to 10 embeds.
    /// Returns the Discord message ID on success, null on failure.
    /// </summary>
    public async Task<string?> SendEmbedsAsync(
        IReadOnlyList<DiscordEmbed> embeds,
        CancellationToken ct = default)
    {
        if (!settings.IsConfigured)
        {
            logger.LogDebug("Discord not configured — skipping webhook send");
            return null;
        }

        // Discord allows max 10 embeds per message
        if (embeds.Count > 10)
            throw new ArgumentException("Discord allows at most 10 embeds per message.", nameof(embeds));

        var payload = new
        {
            username   = settings.Username,
            avatar_url = settings.AvatarUrl,
            embeds,
        };

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var client = httpFactory.CreateClient("discord");

        // ?wait=true makes Discord return the created message JSON (so we can get the ID)
        var url = settings.WebhookUrl.TrimEnd('/') + "?wait=true";

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(url, content, ct);

        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("Discord webhook returned {Status}: {Body}", (int)resp.StatusCode, body);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("id").GetString();
        }
        catch
        {
            // Message posted but ID extraction failed — treat as success
            return "ok";
        }
    }
}

// ── Embed model ──────────────────────────────────────────────────────────────

public sealed record DiscordEmbed(
    string Title,
    string Url,
    string? Description,
    int Color,
    DiscordEmbedThumbnail? Thumbnail,
    DiscordEmbedFooter? Footer);

public sealed record DiscordEmbedThumbnail(string Url);

public sealed record DiscordEmbedFooter(string Text, string? IconUrl = null);
