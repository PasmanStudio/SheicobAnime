namespace AnimeIndex.Api.Data.Entities;

public class Mirror
{
    public Guid Id { get; set; }
    public Guid EpisodeId { get; set; }
    public string ProviderName { get; set; } = null!;
    public string EmbedUrl { get; set; } = null!;
    public short QualityLabel { get; set; } = 720;
    public short Priority { get; set; }
    public bool IsActive { get; set; } = true;
    public short ConsecutiveFailures { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    // Navigation
    public Episode Episode { get; set; } = null!;
}
