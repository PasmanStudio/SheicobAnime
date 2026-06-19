namespace AnimeIndex.Api.DTOs;

public record EpisodeDto(
    Guid Id,
    Guid SeriesId,
    short EpisodeNumber,
    string? Title,
    string? ThumbnailUrl,
    short? DurationSecs,
    DateTime? AiredAt,
    bool IsPublished,
    DateTime CreatedAt,
    string? ImdbId = null,
    decimal? ImdbRating = null,
    int? ImdbVotes = null,
    SeriesStubDto? Series = null,
    MirrorDto[]? Mirrors = null);
