namespace AnimeIndex.Scraper;

/// <summary>In-memory carriers for scraped data — not EF entities.</summary>
public record SeriesScrapedData(
    string Slug,
    string Title,
    string? CoverUrl,
    string? Status,
    string? Type);

public record EpisodeScrapedData(
    Guid SeriesId,
    short EpisodeNumber,
    string? Title,
    IReadOnlyList<MirrorScrapedData> PendingMirrors);

public record MirrorScrapedData(
    Guid EpisodeId,
    string ProviderName,
    string EmbedUrl,
    short QualityLabel,
    short Priority);
