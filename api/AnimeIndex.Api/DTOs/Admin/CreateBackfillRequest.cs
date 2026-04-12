namespace AnimeIndex.Api.DTOs.Admin;

public record CreateBackfillRequest(string Source = "source2", int MaxPages = 200);
