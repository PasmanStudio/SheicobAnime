namespace AnimeIndex.Api.Data.Entities;

public class DiscordPost
{
    public Guid Id { get; set; }
    public Guid EpisodeId { get; set; }
    public string? DiscordMessageId { get; set; }
    public string Status { get; set; } = null!;       // "published" | "failed"
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }

    public Episode Episode { get; set; } = null!;
}
