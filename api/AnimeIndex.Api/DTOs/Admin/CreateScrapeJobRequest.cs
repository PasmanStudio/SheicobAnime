namespace AnimeIndex.Api.DTOs.Admin;

public record CreateScrapeJobRequest(string SourceUrl, string Source = "source1", bool ForceRefresh = false);
