namespace AnimeIndex.Api.Infrastructure.Scraping;

public record ScrapeResult(
    bool Success,
    string? ErrorMessage = null,
    int SeriesIndexed = 0,
    int EpisodesIndexed = 0,
    int MirrorsIndexed = 0
);
