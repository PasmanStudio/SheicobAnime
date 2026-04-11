namespace AnimeIndex.Api.Infrastructure.Scraping;

/// <summary>
/// The ONLY interface the scheduler calls for scrape operations.
/// Never instantiate concrete strategies directly — always resolve via DI.
/// </summary>
public interface IScrapeStrategy
{
    /// <summary>Unique key identifying the source (e.g., "source1").</summary>
    string SourceKey { get; }

    /// <summary>
    /// Scrape episodes and mirrors for the given scrape job.
    /// Must check blocked_slugs before writing any data.
    /// Must call MirrorProbeService.IsEmbeddableAsync() for every URL.
    /// </summary>
    Task<ScrapeResult> ScrapeAsync(Guid scrapeJobId, CancellationToken ct = default);
}
