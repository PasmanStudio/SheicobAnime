namespace AnimeIndex.Api.Data.Entities;

public class Series
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string? TitleRomaji { get; set; }
    public string? TitleNative { get; set; }
    public string? Synopsis { get; set; }
    public string? CoverUrl { get; set; }
    public string? BannerUrl { get; set; }
    public short? Year { get; set; }
    public string? Status { get; set; } // ongoing, completed, upcoming, hiatus
    public string? Type { get; set; } // tv, movie, ova, ona, special
    public decimal? Score { get; set; }
    public short? EpisodeCount { get; set; }
    public string? Studio { get; set; }
    public string? Season { get; set; }
    public string? Demographics { get; set; }
    public string? Language { get; set; }
    public short? DurationMinutes { get; set; }
    public string? AiredDate { get; set; }
    public string? Quality { get; set; }
    public string Metadata { get; set; } = "{}"; // JSONB
    public DateTime? LastScrapedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public ICollection<Episode> Episodes { get; set; } = [];
    public ICollection<SeriesGenre> SeriesGenres { get; set; } = [];
}
