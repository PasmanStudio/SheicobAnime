namespace AnimeIndex.Scraper.Infrastructure.Discord;

public class DiscordSettings
{
    /// <summary>
    /// Discord Incoming Webhook URL.
    /// Format: https://discord.com/api/webhooks/{id}/{token}
    /// Create one in: Server Settings → Integrations → Webhooks → New Webhook.
    /// Set via Discord__WebhookUrl secret.
    /// </summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>Public site URL used in embed links.</summary>
    public string SiteUrl { get; set; } = "https://sheicobanime.vercel.app";

    /// <summary>Display name shown as the webhook sender in Discord.</summary>
    public string Username { get; set; } = "SheicobAnime";

    /// <summary>
    /// URL of the avatar image shown next to the webhook message.
    /// Defaults to a placeholder; override via Discord__AvatarUrl secret.
    /// </summary>
    public string AvatarUrl { get; set; } = "https://sheicobanime.vercel.app/favicon.ico";

    /// <summary>Accent color for embed side-bar (decimal of #6366f1 indigo).</summary>
    public int EmbedColor { get; set; } = 6511345; // #6366f1

    public bool IsConfigured => !string.IsNullOrWhiteSpace(WebhookUrl);
}
