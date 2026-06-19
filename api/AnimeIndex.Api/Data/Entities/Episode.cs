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

    // ── IMDb linking (resolved best-effort by the scraper) ──
    /// <summary>Episode-level IMDb id (ttXXXX) — the page where users rate this episode.</summary>
    public string? ImdbId { get; set; }
    /// <summary>Cached IMDb rating (0–10) from OMDb, refreshed periodically.</summary>
    public decimal? ImdbRating { get; set; }
    /// <summary>Cached IMDb vote count from OMDb.</summary>
    public int? ImdbVotes { get; set; }
    /// <summary>When the IMDb rating was last refreshed from OMDb.</summary>
    public DateTime? ImdbCheckedAt { get; set; }

    // Navigation
    public Series Series { get; set; } = null!;
    public ICollection<Mirror> Mirrors { get; set; } = [];
}
