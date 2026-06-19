namespace AnimeIndex.Scraper.Infrastructure.Imdb;

/// <summary>
/// Config for IMDb/TMDB linking. Bound from the "Imdb" section
/// (e.g. Imdb__TmdbApiKey via secret TMDB_API_KEY, Imdb__OmdbApiKey via OMDB_API_KEY).
///
/// TMDB resolves series → tmdb_id → per-episode IMDb id (the bridge; IMDb has no public API
/// for this). OMDb fills the cached IMDb rating shown in the UI. Both have free tiers; when a
/// key is missing that part is skipped gracefully — same best-effort contract as the rest.
/// </summary>
public class ImdbSettings
{
    /// <summary>TMDB v3 API key (32-char hex) OR v4 Read Access Token (JWT). Empty = linking disabled.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>OMDb API key (free, 1000/day). Empty = ratings not fetched (links still work).</summary>
    public string OmdbApiKey { get; set; } = string.Empty;

    /// <summary>Series to (re)resolve to TMDB per run.</summary>
    public int SeriesBatch { get; set; } = 40;

    /// <summary>Episodes to resolve to an IMDb id per run.</summary>
    public int EpisodeBatch { get; set; } = 120;

    /// <summary>Episode ratings to refresh from OMDb per run (keep under the 1000/day free cap).</summary>
    public int RatingBatch { get; set; } = 150;

    /// <summary>How often to refresh a cached IMDb rating.</summary>
    public int RatingRefreshDays { get; set; } = 7;

    /// <summary>How long before retrying a series that didn't resolve to TMDB.</summary>
    public int SeriesRetryDays { get; set; } = 30;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(TmdbApiKey);

    /// <summary>v4 Read Access Tokens are JWTs (sent as a Bearer header); v3 keys go in the query string.</summary>
    public bool TmdbIsBearer => TmdbApiKey.StartsWith("eyJ", StringComparison.Ordinal) || TmdbApiKey.Length > 40;
}
