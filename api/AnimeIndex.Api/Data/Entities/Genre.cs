namespace AnimeIndex.Api.Data.Entities;

public class Genre
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;

    // Navigation
    public ICollection<SeriesGenre> SeriesGenres { get; set; } = [];
}
