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
            .OrderBy(s => s.ImdbResolvedAt)
            .Take(settings.SeriesBatch)
            .ToListAsync(ct);

        var resolved = 0;
        foreach (var s in series)
        {
            if (ct.IsCancellationRequested) break;
            s.ImdbResolvedAt = DateTime.UtcNow; // mark attempted regardless of outcome
            try
            {
                var imdbId = await FindSeriesImdbIdAsync(s, ct);
                if (imdbId is not null)
                {
                    s.ImdbId = imdbId;
                    resolved++;
                    logger.LogDebug("Imdb: {Title} → {Imdb}", s.Title, imdbId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Imdb: series resolve failed for {Title}", s.Title);
            }
        }

        if (series.Count > 0) await db.SaveChangesAsync(ct);
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

    private async Task<int> ResolveEpisodesAsync(CancellationToken ct)
    {
        var retryCutoff = DateTime.UtcNow.AddDays(-settings.RetryDays);
        var episodes = await db.Episodes
            .Include(e => e.Series)
            .Where(e => e.ImdbId == null
                     && e.Series.ImdbId != null
                     && (e.ImdbCheckedAt == null || e.ImdbCheckedAt < retryCutoff))
            .OrderBy(e => e.ImdbCheckedAt)
            .Take(settings.EpisodeBatch)
            .ToListAsync(ct);

        var resolved = 0;
        foreach (var e in episodes)
        {
            if (ct.IsCancellationRequested) break;
            e.ImdbCheckedAt = DateTime.UtcNow; // mark attempted regardless of outcome
            try
            {
                // Season=1 heuristic — correct for the single-cour seasonal anime that dominate
                // the catalog. Long/multi-season shows may not resolve and fall back to the
                // series page (handled in the frontend).
                var json = await OmdbGetAsync(
                    $"i={Uri.EscapeDataString(e.Series.ImdbId!)}&Season=1&Episode={e.EpisodeNumber}", ct);
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Imdb: episode resolve failed for series {Sid} ep {Ep}", e.SeriesId, e.EpisodeNumber);
            }
        }

        if (episodes.Count > 0) await db.SaveChangesAsync(ct);
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
            e.ImdbCheckedAt = DateTime.UtcNow;
            try
            {
                var json = await OmdbGetAsync($"i={Uri.EscapeDataString(e.ImdbId!)}", ct);
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Imdb: rating refresh failed for {Imdb}", e.ImdbId);
            }
        }

        if (episodes.Count > 0) await db.SaveChangesAsync(ct);
        return updated;
    }

    // ── OMDb HTTP ────────────────────────────────────────────────────────────────

    private async Task<string?> OmdbGetAsync(string queryWithoutKey, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("imdb");
        var url  = $"{OmdbBase}?{queryWithoutKey}&apikey={Uri.EscapeDataString(settings.OmdbApiKey)}";
        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsStringAsync(ct);
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
