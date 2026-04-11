namespace AnimeIndex.Api.DTOs.Admin;

public record CreateScrapeJobRequest(string SourceUrl, bool ForceRefresh = false);
