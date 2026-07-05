namespace AnimeIndex.Api.Data.Entities;

/// <summary>
/// Represents one anime news item fetched from an RSS feed.
/// Tracks both the raw feed data and the Instagram posting status.
/// </summary>
public class AnimeNewsItem
{
    public Guid Id { get; set; }

    /// <summary>Short key identifying the RSS source (e.g. "ann", "mal", "crunchyroll").</summary>
    public string SourceKey { get; set; } = null!;

    /// <summary>RSS item GUID — dedup key per source. Never changes for the same article.</summary>
    public string RssGuid { get; set; } = null!;

    public string Title { get; set; } = null!;

    /// <summary>Short article summary (stripped of HTML tags).</summary>
    public string? Summary { get; set; }

    /// <summary>Best image URL found for this article (media:thumbnail, og:image, etc.).</summary>
    public string? ImageUrl { get; set; }

    public string ArticleUrl { get; set; } = null!;

    public DateTime PublishedAt { get; set; }
    public DateTime FetchedAt { get; set; }

    // ── Instagram posting ─────────────────────────────────────────────────
    // "pending" | "published" | "skipped" | "failed"
    public string IgPostStatus { get; set; } = "pending";
    public string? IgFeedMediaId { get; set; }
    public string? IgStoryMediaId { get; set; }
    /// <summary>IG media id del Reel diario de noticias — null si esta noticia no fue el reel del día.</summary>
    public string? IgReelMediaId { get; set; }
    public DateTime? IgPostedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
