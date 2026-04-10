namespace AnimeIndex.Api.Data.Entities;

public class SeriesGenre
{
    public Guid SeriesId { get; set; }
    public int GenreId { get; set; }

    // Navigation
    public Series Series { get; set; } = null!;
    public Genre Genre { get; set; } = null!;
}
