namespace AnimeIndex.Api.Data.Entities;

public class BlockedSlug
{
    public string Slug { get; set; } = null!;
    public string? Reason { get; set; }
    public DateTime BlockedAt { get; set; }
}
