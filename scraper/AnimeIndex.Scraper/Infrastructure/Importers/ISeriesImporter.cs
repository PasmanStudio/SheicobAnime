namespace AnimeIndex.Scraper.Infrastructure.Importers;

/// <summary>
/// A single search hit from a source — just enough to fetch the full series.
/// </summary>
public sealed record SourceSeriesRef(string Slug, string? Title);

/// <summary>
/// Series metadata plus the episode numbers available at the source.
/// Fields map 1:1 to <see cref="SeriesScrapedData"/> so the orchestrator can
/// upsert without source-specific knowledge.
/// </summary>
public sealed record SourceSeries(
    string Slug,
    string Title,
    string? Synopsis,
    string? CoverUrl,
    string? Status,                       // "ongoing" | "completed" | "upcoming"
    string? Type,                         // "tv" | "movie" | "ova" | "ona" | "special"
    short? Year,
    IReadOnlyList<string>? Genres,
    IReadOnlyList<short> EpisodeNumbers);

/// <summary>One playable embed for an episode, already filtered to the chosen audio.</summary>
public sealed record SourceEmbed(string Server, string Url);

/// <summary>
/// Per-series importer for a single source (animeav1, etc.). This is the ONLY
/// interface <see cref="SeriesImportService"/> calls to pull data from a source.
/// Keep it source-agnostic: all DB writes, provider mapping and uploads live in
/// the orchestrator, never in the importer.
///
/// Distinct from <c>IScrapeStrategy</c> (the daily directory crawl): an importer
/// is query-driven and pulls exactly one series on demand. Add a new source by
/// implementing this interface and registering it as <c>ISeriesImporter</c>.
/// </summary>
public interface ISeriesImporter
{
    /// <summary>Unique key identifying the source (e.g. "animeav1").</summary>
    string SourceKey { get; }

    /// <summary>Searches the source by free text. Returns candidates in relevance order.</summary>
    Task<IReadOnlyList<SourceSeriesRef>> SearchAsync(string query, CancellationToken ct = default);

    /// <summary>Fetches full metadata + the list of episode numbers for a slug. Null if not found.</summary>
    Task<SourceSeries?> FetchSeriesAsync(string slug, CancellationToken ct = default);

    /// <summary>Fetches the playable embeds for one episode (chosen audio only, e.g. SUB).</summary>
    Task<IReadOnlyList<SourceEmbed>> FetchEpisodeEmbedsAsync(
        string slug, short episodeNumber, CancellationToken ct = default);
}
