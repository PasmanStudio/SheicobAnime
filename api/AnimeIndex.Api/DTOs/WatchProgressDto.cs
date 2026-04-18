namespace AnimeIndex.Api.DTOs;

public record WatchProgressDto(
    string EpisodeId,
    string SeriesSlug,
    int PositionSeconds,
    int DurationSeconds,
    bool Completed,
    DateTime UpdatedAt);

public record UpdateProgressRequest(
    int PositionSeconds,
    int DurationSeconds);

public record RecentProgressDto(
    string EpisodeId,
    string SeriesSlug,
    string? SeriesTitle,
    string? SeriesCoverUrl,
    int EpisodeNumber,
    string? EpisodeTitle,
    int PositionSeconds,
    int DurationSeconds,
    bool Completed,
    DateTime UpdatedAt);
