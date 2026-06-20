namespace AnimeIndex.Scraper.Infrastructure.Imdb;

/// <summary>
/// Config for IMDb linking. Bound from the "Imdb" section (e.g. Imdb__OmdbApiKey via secret
/// OMDB_API_KEY — free, 1000/day, no commercial-use restriction unlike TMDB).
///
/// OMDb's "by title + season + episode" lookup (?t=...&Season=N&Episode=N) returns the
/// EXACT per-episode IMDb id directly — no TMDB bridge needed. One call resolves the id
/// AND the rating at once. When the key is missing, linking is skipped gracefully (the UI
/// falls back to an IMDb search link) — same best-effort contract as the rest.
/// </summary>
public class ImdbSettings
{
    /// <summary>OMDb API key (free, 1000/day). Empty = linking disabled entirely.</summary>
    public string OmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Series IMDb ids to attempt per run. Set well above the catalog size on purpose: OMDb's
    /// free 1000 req/day quota is the real cap, and the resolver stops gracefully (without
    /// burning a retry slot) once it hits that wall — see OmdbQuotaExceededException.
    /// </summary>
    public int SeriesBatch { get; set; } = 3000;

    /// <summary>Episodes to resolve to an exact IMDb id (+ rating) per run.</summary>
    public int EpisodeBatch { get; set; } = 3000;

    /// <summary>Episode ratings to refresh (re-fetch by existing imdb_id) per run.</summary>
    public int RatingBatch { get; set; } = 500;

    /// <summary>How often to refresh a cached IMDb rating.</summary>
    public int RatingRefreshDays { get; set; } = 7;

    /// <summary>How long before retrying a series/episode that didn't resolve.</summary>
    public int RetryDays { get; set; } = 30;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(OmdbApiKey);
}
