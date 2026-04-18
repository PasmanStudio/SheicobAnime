namespace AnimeIndex.Api.Data.Entities;

public class WatchProgress
{
    public Guid DeviceId { get; set; }
    public Guid EpisodeId { get; set; }
    public string SeriesSlug { get; set; } = null!;
    public int PositionSeconds { get; set; }
    public int DurationSeconds { get; set; }
    public bool Completed { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation (optional — useful for /recent query)
    public Episode? Episode { get; set; }
}
