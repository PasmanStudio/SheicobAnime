using AnimeIndex.Api.Data;
using AnimeIndex.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.Telegram;

/// <summary>
/// Publishes newly scraped episodes to a Telegram channel via the Bot API.
///
/// Strategy:
///   • Find episodes published in the last 25 hours with no telegram_posts row yet.
///   • Send one message per episode (photo + caption or text-only).
///   • Delay 600ms between sends to respect Telegram rate limits.
///   • Cap at TelegramSettings.MaxPostsPerRun per scraper run (default: 10).
///   • Record one telegram_posts row per episode.
///
/// Called as best-effort from ScrapeOrchestratorJob — exceptions never
/// propagate to the scrape job status.
/// </summary>
public class TelegramPublisherService(
    AppDbContext db,
    TelegramSettings settings,
    TelegramBotClient client,
    ILogger<TelegramPublisherService> logger)
{
    public async Task PublishNewEpisodesAsync(CancellationToken ct = default)
    {
        if (!settings.IsConfigured)
        {
            logger.LogInformation("Telegram not configured — skipping publisher (set Telegram__BotToken)");
            return;
        }

        var since = DateTime.UtcNow.AddHours(-25);

        // Episodes already posted to Telegram
        var alreadyPosted = await db.TelegramPosts
            .Where(p => p.Status == "published")
            .Select(p => p.EpisodeId)
            .Distinct()
            .ToListAsync(ct);

        var episodes = await db.Episodes
            .Include(e => e.Series)
            .Where(e => e.IsPublished
                     && e.CreatedAt >= since
                     && !alreadyPosted.Contains(e.Id))
            .OrderByDescending(e => e.CreatedAt)
            .Take(settings.MaxPostsPerRun)
            .ToListAsync(ct);

        if (episodes.Count == 0)
        {
            logger.LogInformation("No new episodes to post to Telegram");
            return;
        }

        logger.LogInformation("Preparing Telegram notification for {Count} episode(s)", episodes.Count);

        foreach (var episode in episodes)
        {
            if (ct.IsCancellationRequested) break;

            var record = new TelegramPost
            {
                EpisodeId = episode.Id,
                Status    = "failed",
                CreatedAt = DateTime.UtcNow,
            };
            db.TelegramPosts.Add(record);
            await db.SaveChangesAsync(ct);

            var caption = BuildCaption(episode);
            var photoUrl = episode.Series?.CoverUrl;

            try
            {
                var messageId = await client.SendEpisodeAsync(caption, photoUrl, ct);

                if (messageId is null)
                {
                    record.ErrorMessage = "API returned no message ID";
                    logger.LogWarning("Telegram send returned no message ID for episode {EpisodeId}", episode.Id);
                }
                else
                {
                    record.Status           = "published";
                    record.TelegramMessageId = messageId;
                    record.PublishedAt      = DateTime.UtcNow;

                    logger.LogInformation(
                        "Posted Telegram message {MessageId} for {Series} ep {Ep}",
                        messageId, episode.Series?.Title, episode.EpisodeNumber);
                }
            }
            catch (Exception ex)
            {
                record.ErrorMessage = ex.Message[..Math.Min(ex.Message.Length, 500)];
                logger.LogWarning(ex, "Telegram send failed for episode {EpisodeId}", episode.Id);
            }

            await db.SaveChangesAsync(CancellationToken.None);

            // Respect Telegram rate limit: ~30 msg/s globally, but be conservative for channels
            if (episodes.IndexOf(episode) < episodes.Count - 1)
                await Task.Delay(600, ct);
        }
    }

    private string BuildCaption(Episode episode)
    {
        var seriesTitle = episode.Series?.Title ?? "Anime";
        var epLabel     = $"Episodio {episode.EpisodeNumber}";
        var url         = $"{settings.SiteUrl}/series/{episode.Series?.Slug ?? episode.SeriesId.ToString()}/{episode.EpisodeNumber}";

        var lines = new System.Text.StringBuilder();
        lines.AppendLine($"🎌 <b>{HtmlEscape(seriesTitle)}</b> — {epLabel}");

        if (episode.Title is { Length: > 0 } epTitle)
            lines.AppendLine($"<i>{HtmlEscape(epTitle)}</i>");

        lines.AppendLine();
        lines.Append($"▶️ <a href=\"{url}\">Ver ahora en SheicobAnime</a>");

        return lines.ToString().TrimEnd();
    }

    private static string HtmlEscape(string text) =>
        text.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
}
