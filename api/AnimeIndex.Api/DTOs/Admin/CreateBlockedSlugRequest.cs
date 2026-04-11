namespace AnimeIndex.Api.DTOs.Admin;

public record CreateBlockedSlugRequest(string Slug, string? Reason = null);
