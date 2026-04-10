namespace AnimeIndex.Api.Data.Entities;

public class Episode
{
    public Guid Id { get; set; }
    public Guid SeriesId { get; set; }
    public short EpisodeNumber { get; set; }
    public string? Title { get; set; }
    public string? ThumbnailUrl { get; set; }
    public short? DurationSecs { get; set; }
    public DateTime? AiredAt { get; set; }
    public bool IsPublished { get; set; }
    public DateTime CreatedAt { get; set; }

    // Navigation
    public Series Series { get; set; } = null!;
    public ICollection<Mirror> Mirrors { get; set; } = [];
}
