using AnimeIndex.Api.Data;
using AnimeIndex.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.Discord;

/// <summary>
/// Publishes newly scraped episodes to Discord via an Incoming Webhook.
///
/// Strategy:
///   • Find episodes published in the last 25 hours with no discord_posts row yet.
///   • Build one Discord embed per episode (thumbnail + title + direct link).
///   • Send in batches of up to 10 embeds (Discord message limit).
///   • Record one discord_posts row per episode.
///
/// Called as best-effort from ScrapeOrchestratorJob — exceptions never
/// propagate to the scrape job status.
/// </summary>
public class DiscordPublisherService(
    AppDbContext db,
    DiscordSettings settings,
    DiscordWebhookClient client,
    ILogger<DiscordPublisherService> logger)
{
    private const int MaxEmbedsPerMessage = 10;

    public async Task PublishNewEpisodesAsync(CancellationToken ct = default)
    {
        if (!settings.IsConfigured)
        {
            logger.LogInformation("Discord not configured — skipping publisher (set Discord__WebhookUrl)");
            return;
        }

        var since = DateTime.UtcNow.AddHours(-25);

        // Episodes already posted to Discord
        var alreadyPosted = await db.DiscordPosts
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
            .ToListAsync(ct);

        if (episodes.Count == 0)
        {
            logger.LogInformation("No new episodes to post to Discord");
            return;
        }

        logger.LogInformation("Preparing Discord notification for {Count} episode(s)", episodes.Count);

        // Pre-create DB records (all start as failed — updated on success per batch)
        var records = episodes.Select(e => new DiscordPost
        {
            EpisodeId = e.Id,
            Status    = "failed",
            CreatedAt = DateTime.UtcNow,
        }).ToList();
        db.DiscordPosts.AddRange(records);
        await db.SaveChangesAsync(ct);

        // Index records by EpisodeId for easy lookup
        var recordByEpisode = records.ToDictionary(r => r.EpisodeId);

        // Send in batches of MaxEmbedsPerMessage
        var batches = episodes
            .Select((ep, i) => (Episode: ep, Index: i))
            .GroupBy(x => x.Index / MaxEmbedsPerMessage)
            .Select(g => g.Select(x => x.Episode).ToList())
            .ToList();

        foreach (var batch in batches)
        {
            if (ct.IsCancellationRequested) break;

            var embeds = batch.Select(ep => BuildEmbed(ep)).ToList();

            try
            {
                var messageId = await client.SendEmbedsAsync(embeds, ct);

                if (messageId is null)
                {
                    logger.LogWarning("Discord webhook returned no message ID for batch of {Count} episodes", batch.Count);
                    // Records stay as "failed"
                }
                else
                {
                    foreach (var ep in batch)
                    {
                        var record = recordByEpisode[ep.Id];
                        record.Status          = "published";
                        record.DiscordMessageId = messageId;
                        record.PublishedAt     = DateTime.UtcNow;
                    }

                    logger.LogInformation(
                        "Posted Discord batch of {Count} episode(s) → message {MessageId}",
                        batch.Count, messageId);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Discord batch send failed for {Count} episodes", batch.Count);
                // Records stay as "failed" — will be retried next run
            }
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private DiscordEmbed BuildEmbed(Episode episode)
    {
        var seriesTitle = episode.Series?.Title ?? "Anime";
        var epLabel     = $"Episodio {episode.EpisodeNumber}";
        var title       = $"🎬 {seriesTitle} — {epLabel}";
        var url         = $"{settings.SiteUrl}/series/{episode.Series?.Slug ?? episode.SeriesId.ToString()}/{episode.EpisodeNumber}";

        var description = episode.Title is { Length: > 0 } t
            ? $"**{t}**\n▶️ Ver ahora gratis en SheicobAnime"
            : "▶️ Ver ahora gratis en SheicobAnime";

        var thumbnail = episode.Series?.CoverUrl is { Length: > 0 } coverUrl
            ? new DiscordEmbedThumbnail(coverUrl)
            : null;

        return new DiscordEmbed(
            Title:       title,
            Url:         url,
            Description: description,
            Color:       settings.EmbedColor,
            Thumbnail:   thumbnail,
            Footer:      new DiscordEmbedFooter("SheicobAnime • Nueva emisión"));
    }
}
