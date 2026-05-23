namespace AnimeIndex.Api.Data.Entities;

public class TelegramPost
{
    public Guid Id { get; set; }
    public Guid EpisodeId { get; set; }
    public string? TelegramMessageId { get; set; }
    public string Status { get; set; } = null!;       // "published" | "failed"
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }

    public Episode Episode { get; set; } = null!;
}
