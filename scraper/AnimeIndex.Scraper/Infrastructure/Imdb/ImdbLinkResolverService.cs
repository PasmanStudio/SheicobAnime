using System.Globalization;
using System.Text.Json;
using AnimeIndex.Api.Data;
using AnimeIndex.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.Imdb;

/// <summary>
/// Resolves IMDb links for series/episodes and caches IMDb ratings, all via OMDb alone —
/// no TMDB bridge needed (TMDB's commercial-use terms are a poor fit for an ad-supported site).
///
///   1. Series → IMDb id, via OMDb title lookup (?t={title}&y={year}).
///   2. Episode → EXACT IMDb id + rating in one call, via OMDb's by-title-or-id season/episode
///      lookup (?i={seriesImdbId}&Season=1&Episode={n}) — season=1 heuristic for 1-cour anime.
///   3. Stale rating refresh, via OMDb by episode id (?i={episodeImdbId}).
///
/// IMDb itself has no public API to read or write this — OMDb is a free, non-commercial-
/// restricted read-only mirror of IMDb data. Nothing here ever submits votes; the UI deep-links
/// users to the exact episode page to rate it themselves.
///
/// Batch sizes are set generously (covering the whole catalog) on purpose: OMDb's free tier
/// (1000 req/day) is the real bottleneck, not these limits. <see cref="OmdbQuotaExceededException"/>
/// stops a run early WITHOUT marking the untried remainder as "attempted", so they're picked up
/// — at the front of the queue — on the next run instead of waiting out a 30-day retry cooldown.
/// </summary>
public class ImdbLinkResolverService(
    AppDbContext db,
    IHttpClientFactory httpFactory,
    ImdbSettings settings,
    ILogger<ImdbLinkResolverService> logger)
{
    private const string OmdbBase = "https://www.omdbapi.com/";

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (!settings.IsEnabled)
        {
            logger.LogInformation("Imdb: OMDb key not configured — skipping IMDb linking");
            return;
        }

        var series = await ResolveSeriesAsync(ct);
        var eps    = await ResolveEpisodesAsync(ct);
        var rated  = await RefreshStaleRatingsAsync(ct);
        logger.LogInformation("Imdb: resolved {Series} series, {Eps} episodes (id+rating), refreshed {Rated} ratings",
            series, eps, rated);
    }

    // ── 1. Series → IMDb id ───────────────────────────────────────────────────────

    private async Task<int> ResolveSeriesAsync(CancellationToken ct)
    {
        var retryCutoff = DateTime.UtcNow.AddDays(-settings.RetryDays);
        var series = await db.Series
            .Where(s => s.ImdbId == null && (s.ImdbResolvedAt == null || s.ImdbResolvedAt < retryCutoff))
            // Never-attempted series first (HasValue==false sorts before true), then oldest retry.
            .OrderBy(s => s.ImdbResolvedAt.HasValue)
            .ThenBy(s => s.ImdbResolvedAt)
            .Take(settings.SeriesBatch)
            .ToListAsync(ct);

        var resolved = 0;
        foreach (var s in series)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var imdbId = await FindSeriesImdbIdAsync(s, ct);
                s.ImdbResolvedAt = DateTime.UtcNow; // only mark attempted on a REAL attempt
                if (imdbId is not null)
                {
                    s.ImdbId = imdbId;
                    resolved++;
                    logger.LogDebug("Imdb: {Title} → {Imdb}", s.Title, imdbId);
                }
            }
            catch (OmdbQuotaExceededException)
            {
                logger.LogWarning(
                    "Imdb: OMDb daily quota reached while resolving series ({Resolved} done) — stopping early, retried first next run",
                    resolved);
                break; // leave s.ImdbResolvedAt untouched — retried (with priority) tomorrow
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                s.ImdbResolvedAt = DateTime.UtcNow; // genuine failure (bad data, parse error) — don't retry for 30d
                logger.LogDebug(ex, "Imdb: series resolve failed for {Title}", s.Title);
            }
        }

        await db.SaveChangesAsync(ct);
        return resolved;
    }

    private async Task<string?> FindSeriesImdbIdAsync(Series s, CancellationToken ct)
    {
        foreach (var title in CandidateTitles(s))
        {
            var json = await OmdbGetAsync($"t={Uri.EscapeDataString(title)}&type=series&y={s.Year}", ct)
                       ?? await OmdbGetAsync($"t={Uri.EscapeDataString(title)}&type=series", ct);
            if (json is null) continue;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("Response", out var ok) && ok.GetString() == "False") continue;
            if (root.TryGetProperty("imdbID", out var idEl) && idEl.GetString() is { Length: > 0 } id)
                return id;
        }
        return null;
    }

    private static IEnumerable<string> CandidateTitles(Series s)
    {
        // Some scraped titles carry literal HTML entities (e.g. "&#039;") — decode them,
        // otherwise OMDb's API can 500 on the raw ampersand sequence.
        if (!string.IsNullOrWhiteSpace(s.TitleRomaji)) yield return System.Net.WebUtility.HtmlDecode(s.TitleRomaji!);
        if (!string.IsNullOrWhiteSpace(s.Title))       yield return System.Net.WebUtility.HtmlDecode(s.Title);
        if (!string.IsNullOrWhiteSpace(s.TitleNative)) yield return System.Net.WebUtility.HtmlDecode(s.TitleNative!);
    }

    // ── 2. Episode → exact IMDb id + rating (one OMDb call) ───────────────────────

    /// <summary>
    /// Above this episode count, the season=1 heuristic is known-futile (e.g. Naruto ep 191
    /// has no season-1 episode 191) — every attempt would just fail and burn quota. These long
    /// multi-season shows are excluded here; they still get the series-level fallback link.
    /// </summary>
    private const int MaxEpisodeCountForSeasonOneHeuristic = 30;

    private async Task<int> ResolveEpisodesAsync(CancellationToken ct)
    {
        var retryCutoff = DateTime.UtcNow.AddDays(-settings.RetryDays);
        var episodes = await db.Episodes
            .Include(e => e.Series)
            .Where(e => e.ImdbId == null
                     && e.Series.ImdbId != null
                     && (e.Series.EpisodeCount == null || e.Series.EpisodeCount <= MaxEpisodeCountForSeasonOneHeuristic)
                     && (e.ImdbCheckedAt == null || e.ImdbCheckedAt < retryCutoff))
            .OrderBy(e => e.ImdbCheckedAt.HasValue)
            .ThenBy(e => e.ImdbCheckedAt)
            .Take(settings.EpisodeBatch)
            .ToListAsync(ct);

        var resolved = 0;
        foreach (var e in episodes)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // Season=1 heuristic — correct for the single-cour seasonal anime that dominate
                // the catalog. Long/multi-season shows may not resolve and fall back to the
                // series page (handled in the frontend).
                var json = await OmdbGetAsync(
                    $"i={Uri.EscapeDataString(e.Series.ImdbId!)}&Season=1&Episode={e.EpisodeNumber}", ct);
                e.ImdbCheckedAt = DateTime.UtcNow; // only mark attempted on a REAL attempt
                if (json is null) continue;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("Response", out var ok) && ok.GetString() == "False") continue;

                if (root.TryGetProperty("imdbID", out var idEl) && idEl.GetString() is { Length: > 0 } id)
                {
                    e.ImdbId = id;
                    (e.ImdbRating, e.ImdbVotes) = ParseRating(root);
                    resolved++;
                }
            }
            catch (OmdbQuotaExceededException)
            {
                logger.LogWarning(
                    "Imdb: OMDb daily quota reached while resolving episodes ({Resolved} done) — stopping early, retried first next run",
                    resolved);
                break; // leave e.ImdbCheckedAt untouched — retried (with priority) tomorrow
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                e.ImdbCheckedAt = DateTime.UtcNow; // genuine failure — don't retry for 30d
                logger.LogDebug(ex, "Imdb: episode resolve failed for series {Sid} ep {Ep}", e.SeriesId, e.EpisodeNumber);
            }
        }

        await db.SaveChangesAsync(ct);
        return resolved;
    }

    // ── 3. Stale rating refresh (by already-known episode IMDb id) ────────────────

    private async Task<int> RefreshStaleRatingsAsync(CancellationToken ct)
    {
        var staleCutoff = DateTime.UtcNow.AddDays(-settings.RatingRefreshDays);
        var episodes = await db.Episodes
            .Where(e => e.ImdbId != null && e.ImdbCheckedAt < staleCutoff)
            .OrderBy(e => e.ImdbCheckedAt)
            .Take(settings.RatingBatch)
            .ToListAsync(ct);

        var updated = 0;
        foreach (var e in episodes)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var json = await OmdbGetAsync($"i={Uri.EscapeDataString(e.ImdbId!)}", ct);
                e.ImdbCheckedAt = DateTime.UtcNow;
                if (json is null) continue;

                using var doc = JsonDocument.Parse(json);
                var (rating, votes) = ParseRating(doc.RootElement);
                if (rating is not null)
                {
                    e.ImdbRating = rating;
                    e.ImdbVotes  = votes;
                    updated++;
                }
            }
            catch (OmdbQuotaExceededException)
            {
                logger.LogWarning(
                    "Imdb: OMDb daily quota reached while refreshing ratings ({Updated} done) — stopping early", updated);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                e.ImdbCheckedAt = DateTime.UtcNow;
                logger.LogDebug(ex, "Imdb: rating refresh failed for {Imdb}", e.ImdbId);
            }
        }

        await db.SaveChangesAsync(ct);
        return updated;
    }

    // ── OMDb HTTP ────────────────────────────────────────────────────────────────

    /// <summary>Thrown when OMDb's daily free-tier quota is exhausted (body contains
    /// "Request limit reached"). Callers stop the current loop without marking the
    /// in-flight item as attempted, so it's retried — with priority — on the next run.</summary>
    private sealed class OmdbQuotaExceededException : Exception;

    private async Task<string?> OmdbGetAsync(string queryWithoutKey, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("imdb");
        var url  = $"{OmdbBase}?{queryWithoutKey}&apikey={Uri.EscapeDataString(settings.OmdbApiKey)}";
        using var resp = await http.GetAsync(url, ct);

        // OMDb signals daily-quota exhaustion with HTTP 401 (NOT a 200 + error body) —
        // read the body and check for the quota message BEFORE looking at the status code,
        // otherwise every remaining item in the batch gets silently (and wrongly) treated
        // as "no match" and marked attempted, burning the 30-day retry window for nothing.
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (body.Contains("Request limit reached", StringComparison.OrdinalIgnoreCase))
            throw new OmdbQuotaExceededException();

        if (!resp.IsSuccessStatusCode) return null;
        return body;
    }

    private static (decimal? rating, int? votes) ParseRating(JsonElement root)
    {
        decimal? rating = null;
        if (root.TryGetProperty("imdbRating", out var rEl) && rEl.GetString() is { } rs && rs != "N/A"
            && decimal.TryParse(rs, NumberStyles.Number, CultureInfo.InvariantCulture, out var rv))
            rating = rv;

        int? votes = null;
        if (root.TryGetProperty("imdbVotes", out var vEl) && vEl.GetString() is { } vs && vs != "N/A"
            && int.TryParse(vs.Replace(",", ""), out var vv))
            votes = vv;

        return (rating, votes);
    }
}
