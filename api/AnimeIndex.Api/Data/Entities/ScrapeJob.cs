namespace AnimeIndex.Api.Data.Entities;

public class ScrapeJob
{
    public Guid Id { get; set; }
    public Guid? SeriesId { get; set; }
    public string JobType { get; set; } = null!;
    public string Status { get; set; } = "pending"; // pending, running, completed, failed, dead_letter
    public short AttemptCount { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime ScheduledAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Navigation
    public Series? Series { get; set; }
}
