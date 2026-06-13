namespace AnimeIndex.Scraper.Infrastructure.Telegram;

public class TelegramSettings
{
    /// <summary>
    /// Telegram Bot API token from @BotFather.
    /// Format: 1234567890:AAF...
    /// Set via Telegram__BotToken secret.
    /// </summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// Public channel username (with @) or numeric chat ID.
    /// Example: @sheicobanime
    /// </summary>
    public string ChannelId { get; set; } = "@sheicobanime";

    /// <summary>Public site URL used in message links.</summary>
    public string SiteUrl { get; set; } = "https://sheicobanime.sheicob.workers.dev";

    /// <summary>Max episodes to post per scraper run (avoids flooding the channel).</summary>
    public int MaxPostsPerRun { get; set; } = 25;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken);
}
