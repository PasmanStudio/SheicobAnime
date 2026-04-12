namespace AnimeIndex.Api.DTOs;

public record SeriesDto(
    Guid Id,
    string Slug,
    string Title,
    string? TitleRomaji,
    string? TitleNative,
    string? Synopsis,
    string? CoverUrl,
    string? BannerUrl,
    short? Year,
    string? Status,
    string? Type,
    decimal? Score,
    short? EpisodeCount,
    GenreDto[] Genres,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record SeriesStubDto(
    Guid Id,
    string Slug,
    string Title,
    string? CoverUrl);

public record SeriesSuggestDto(
    string Slug,
    string Title,
    string? CoverUrl,
    string? Type,
    string? Status);
