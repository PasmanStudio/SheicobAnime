namespace AnimeIndex.Scraper;

/// <summary>In-memory carriers for scraped data — not EF entities.</summary>
public record SeriesScrapedData(
    string Slug,
    string Title,
    string? CoverUrl,
    string? Status,
    string? Type,
    string? Synopsis = null,
    string? TitleRomaji = null,
    string? TitleNative = null,
    decimal? Score = null,
    short? Year = null,
    short? EpisodeCount = null,
    IReadOnlyList<string>? Genres = null,
    string? Studio = null,
    string? Season = null,
    string? Demographics = null,
    string? Language = null,
    short? DurationMinutes = null,
    string? AiredDate = null,
    string? Quality = null);

public record EpisodeScrapedData(
    Guid SeriesId,
    short EpisodeNumber,
    string? Title,
    IReadOnlyList<MirrorScrapedData> PendingMirrors,
    DateTime? AiredAt = null);

public record MirrorScrapedData(
    Guid EpisodeId,
    string ProviderName,
    string EmbedUrl,
    short QualityLabel,
    short Priority);
