namespace AnimeIndex.Api.Data.Entities;

public class InstagramPost
{
    public Guid Id { get; set; }
    public Guid EpisodeId { get; set; }
    public string PostType { get; set; } = null!;   // "story" | "feed"
    public string Status { get; set; } = null!;      // "published" | "failed" | "skipped"
    public string? IgMediaId { get; set; }
    public string? Caption { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }

    public Episode Episode { get; set; } = null!;
}
