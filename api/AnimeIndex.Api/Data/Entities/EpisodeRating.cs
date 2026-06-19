namespace AnimeIndex.Api.Data.Entities;

/// <summary>
/// A native per-episode star rating (1–5), keyed by device id so it works for
/// anonymous and logged-in viewers alike — same identity model as WatchProgress.
/// One row per (device, episode); re-rating updates the existing row.
/// </summary>
public class EpisodeRating
{
    public Guid DeviceId { get; set; }
    public Guid EpisodeId { get; set; }
    public short Rating { get; set; } // 1..5
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Episode Episode { get; set; } = null!;
}
