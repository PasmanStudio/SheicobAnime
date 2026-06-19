using System.Globalization;
using System.Text;
using System.Text.Json;
using AnimeIndex.Api.Data;
using AnimeIndex.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.Imdb;

/// <summary>
/// Resolves IMDb links for series/episodes and caches IMDb ratings, all best-effort.
///
///   1. Series → TMDB tv id (title search) + series IMDb id.
///   2. Episode → IMDb id via TMDB episode external_ids (season=1 heuristic for 1-cour anime).
///   3. Episode IMDb rating via OMDb (cached, refreshed every few days).
///
/// IMDb has no public API to read this directly, so TMDB is the bridge. Nothing here ever
/// submits votes — that's impossible via API; the UI just deep-links users to IMDb to rate.
/// </summary>
public class ImdbLinkResolverService(
    AppDbContext db,
    IHttpClientFactory httpFactory,
    ImdbSettings settings,
    ILogger<ImdbLinkResolverService> logger)
{
    private const string TmdbBase = "https://api.themoviedb.org/3";

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (!settings.IsEnabled)
        {
            logger.LogInformation("Imdb: TMDB key not configured — skipping IMDb linking");
            return;
        }

        var series = await ResolveSeriesAsync(ct);
        var eps    = await ResolveEpisodesAsync(ct);
        var rated  = await RefreshRatingsAsync(ct);
        logger.LogInformation("Imdb: resolved {Series} series, {Eps} episode links, {Rated} ratings", series, eps, rated);
    }

    // ── 1. Series → TMDB id + series IMDb id ─────────────────────────────────────

    private async Task<int> ResolveSeriesAsync(CancellationToken ct)
    {
        var retryCutoff = DateTime.UtcNow.AddDays(-settings.SeriesRetryDays);
        var series = await db.Series
            .Where(s => s.TmdbId == null && (s.ImdbResolvedAt == null || s.ImdbResolvedAt < retryCutoff))
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
                var tmdbId = await SearchTmdbTvAsync(s, ct);
                if (tmdbId is not null)
                {
                    s.TmdbId = tmdbId;
                    s.ImdbId = await GetExternalImdbIdAsync($"/tv/{tmdbId}/external_ids", ct);
                    resolved++;
                    logger.LogDebug("Imdb: {Title} → tmdb {Tmdb} / imdb {Imdb}", s.Title, tmdbId, s.ImdbId);
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

    private async Task<int?> SearchTmdbTvAsync(Series s, CancellationToken ct)
    {
        foreach (var (query, withYear) in CandidateQueries(s))
        {
            var results = await TmdbSearchTvAsync(query, withYear ? s.Year : null, ct);
            if (results.Count == 0) continue;

            var best = results
                .Select(r => (r.Id, Score: ScoreMatch(r, s)))
                .OrderByDescending(x => x.Score)
                .First();

            if (best.Score >= 0.3) return best.Id; // confident enough; else try next variant
        }
        return null;
    }

    private static IEnumerable<(string Query, bool WithYear)> CandidateQueries(Series s)
    {
        if (!string.IsNullOrWhiteSpace(s.TitleRomaji)) { yield return (s.TitleRomaji!, true); yield return (s.TitleRomaji!, false); }
        if (!string.IsNullOrWhiteSpace(s.Title))       { yield return (s.Title, true); }
        if (!string.IsNullOrWhiteSpace(s.TitleNative))  { yield return (s.TitleNative!, false); }
        if (!string.IsNullOrWhiteSpace(s.Title))        { yield return (s.Title, false); }
    }

    /// <summary>Match confidence: title-token overlap (max over our title variants) + a year bonus.</summary>
    private static double ScoreMatch(TmdbTv r, Series s)
    {
        var names = new[] { r.Name, r.OriginalName }.Where(n => !string.IsNullOrWhiteSpace(n))!.Cast<string>();
        var ours  = new[] { s.TitleRomaji, s.Title, s.TitleNative }.Where(n => !string.IsNullOrWhiteSpace(n))!.Cast<string>();

        double best = 0;
        foreach (var a in names)
            foreach (var b in ours)
                best = Math.Max(best, TokenSimilarity(a, b));

        if (s.Year is { } y && r.Year is { } ry && Math.Abs(y - ry) <= 1) best += 0.25;
        return best;
    }

    // ── 2. Episode → IMDb id (TMDB episode external_ids) ─────────────────────────

    private async Task<int> ResolveEpisodesAsync(CancellationToken ct)
    {
        var retryCutoff = DateTime.UtcNow.AddDays(-settings.SeriesRetryDays);
        var episodes = await db.Episodes
            .Include(e => e.Series)
            .Where(e => e.ImdbId == null
                     && e.Series.TmdbId != null
                     && (e.ImdbCheckedAt == null || e.ImdbCheckedAt < retryCutoff))
            .OrderBy(e => e.ImdbCheckedAt)
            .Take(settings.EpisodeBatch)
            .ToListAsync(ct);

        var resolved = 0;
        foreach (var e in episodes)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // Season=1 heuristic — correct for the single-cour seasonal anime that dominate
                // the catalog. Long/multi-season shows may not resolve and fall back to the series page.
                var imdb = await GetExternalImdbIdAsync(
                    $"/tv/{e.Series.TmdbId}/season/1/episode/{e.EpisodeNumber}/external_ids", ct);

                if (!string.IsNullOrWhiteSpace(imdb))
                {
                    e.ImdbId = imdb;
                    e.ImdbCheckedAt = null; // leave null so RefreshRatings fetches the rating next
                    resolved++;
                }
                else
                {
                    e.ImdbCheckedAt = DateTime.UtcNow; // don't retry resolution every run
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Imdb: episode resolve failed for series {Sid} ep {Ep}", e.SeriesId, e.EpisodeNumber);
                e.ImdbCheckedAt = DateTime.UtcNow;
            }
        }

        if (episodes.Count > 0) await db.SaveChangesAsync(ct);
        return resolved;
    }

    // ── 3. Episode IMDb rating via OMDb ──────────────────────────────────────────

    private async Task<int> RefreshRatingsAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.OmdbApiKey)) return 0;

        var staleCutoff = DateTime.UtcNow.AddDays(-settings.RatingRefreshDays);
        var episodes = await db.Episodes
            .Where(e => e.ImdbId != null && (e.ImdbCheckedAt == null || e.ImdbCheckedAt < staleCutoff))
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
                var (rating, votes) = await OmdbRatingAsync(e.ImdbId!, ct);
                if (rating is not null)
                {
                    e.ImdbRating = rating;
                    e.ImdbVotes  = votes;
                    updated++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Imdb: OMDb rating fetch failed for {Imdb}", e.ImdbId);
            }
        }

        if (episodes.Count > 0) await db.SaveChangesAsync(ct);
        return updated;
    }

    // ── TMDB HTTP ────────────────────────────────────────────────────────────────

    private async Task<List<TmdbTv>> TmdbSearchTvAsync(string query, short? year, CancellationToken ct)
    {
        var path = $"/search/tv?query={Uri.EscapeDataString(query)}&include_adult=false";
        if (year is not null) path += $"&first_air_date_year={year}";

        var body = await TmdbGetAsync(path, ct);
        if (body is null) return [];

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("results", out var results)) return [];

        var list = new List<TmdbTv>();
        foreach (var r in results.EnumerateArray())
        {
            if (!r.TryGetProperty("id", out var idEl)) continue;
            short? ry = null;
            if (r.TryGetProperty("first_air_date", out var fad) && fad.GetString() is { Length: >= 4 } d
                && short.TryParse(d[..4], out var yr)) ry = yr;
            list.Add(new TmdbTv(
                idEl.GetInt32(),
                r.TryGetProperty("name", out var n) ? n.GetString() : null,
                r.TryGetProperty("original_name", out var on) ? on.GetString() : null,
                ry));
            if (list.Count >= 10) break;
        }
        return list;
    }

    private async Task<string?> GetExternalImdbIdAsync(string path, CancellationToken ct)
    {
        var body = await TmdbGetAsync(path, ct);
        if (body is null) return null;
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("imdb_id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
        {
            var id = idEl.GetString();
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        return null;
    }

    private async Task<string?> TmdbGetAsync(string pathAndQuery, CancellationToken ct)
    {
        var url = TmdbBase + pathAndQuery;
        if (!settings.TmdbIsBearer)
            url += (pathAndQuery.Contains('?') ? "&" : "?") + "api_key=" + Uri.EscapeDataString(settings.TmdbApiKey);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (settings.TmdbIsBearer)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.TmdbApiKey);

        var http = httpFactory.CreateClient("imdb");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsStringAsync(ct);
    }

    // ── OMDb HTTP ────────────────────────────────────────────────────────────────

    private async Task<(decimal? rating, int? votes)> OmdbRatingAsync(string imdbId, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("imdb");
        var url  = $"https://www.omdbapi.com/?i={Uri.EscapeDataString(imdbId)}&apikey={Uri.EscapeDataString(settings.OmdbApiKey)}";
        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return (null, null);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        if (root.TryGetProperty("Response", out var ok) && ok.GetString() == "False") return (null, null);

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

    // ── Title similarity (de-accented token Jaccard) ─────────────────────────────

    private static double TokenSimilarity(string a, string b)
    {
        var sa = Tokens(a);
        var sb = Tokens(b);
        if (sa.Count == 0 || sb.Count == 0) return 0;
        var inter = sa.Count(sb.Contains);
        return (double)inter / (sa.Count + sb.Count - inter);
    }

    private static HashSet<string> Tokens(string s)
    {
        var decomposed = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2)
            .ToHashSet(StringComparer.Ordinal);
    }

    private sealed record TmdbTv(int Id, string? Name, string? OriginalName, short? Year);
}
