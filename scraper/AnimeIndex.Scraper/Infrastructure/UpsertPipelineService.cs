using AnimeIndex.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AnimeIndex.Scraper.Infrastructure;

/// <summary>
/// Writes scraped data to the database using ON CONFLICT DO UPDATE (upsert).
/// All writes go through this service — never INSERT without conflict handling.
/// </summary>
public class UpsertPipelineService(AppDbContext db)
{
    /// <summary>
    /// Upserts a series. Conflict key: slug.
    /// Returns the series UUID (existing or newly created).
    /// </summary>
    public async Task<Guid> UpsertSeriesAsync(SeriesScrapedData data, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO series (id, slug, title, title_romaji, title_native, synopsis, cover_url, status, type, score, year, episode_count, created_at, updated_at)
            VALUES (gen_random_uuid(), {data.Slug}, {data.Title}, {data.TitleRomaji}, {data.TitleNative}, {data.Synopsis}, {data.CoverUrl}, {data.Status}, {data.Type}, {data.Score}, {data.Year}, {data.EpisodeCount}, now(), now())
            ON CONFLICT (slug) DO UPDATE SET
                title           = EXCLUDED.title,
                title_romaji    = COALESCE(EXCLUDED.title_romaji, series.title_romaji),
                title_native    = COALESCE(EXCLUDED.title_native, series.title_native),
                synopsis        = COALESCE(EXCLUDED.synopsis, series.synopsis),
                cover_url       = COALESCE(EXCLUDED.cover_url, series.cover_url),
                status          = COALESCE(EXCLUDED.status, series.status),
                type            = COALESCE(EXCLUDED.type, series.type),
                score           = COALESCE(EXCLUDED.score, series.score),
                year            = COALESCE(EXCLUDED.year, series.year),
                episode_count   = COALESCE(EXCLUDED.episode_count, series.episode_count),
                updated_at      = now(),
                last_scraped_at = now()
            """, ct);

        var seriesId = await db.Series
            .Where(s => s.Slug == data.Slug)
            .Select(s => s.Id)
            .SingleAsync(ct);

        // Link genres if provided
        if (data.Genres is { Count: > 0 })
        {
            foreach (var genreName in data.Genres)
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO genres (name) VALUES ({genreName})
                    ON CONFLICT (name) DO NOTHING
                    """, ct);

                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO series_genres (series_id, genre_id)
                    SELECT {seriesId}, g.id FROM genres g WHERE g.name = {genreName}
                    ON CONFLICT (series_id, genre_id) DO NOTHING
                    """, ct);
            }
        }

        return seriesId;
    }

    /// <summary>
    /// Upserts an episode. Conflict key: (series_id, episode_number).
    /// Returns the episode UUID.
    /// </summary>
    public async Task<Guid> UpsertEpisodeAsync(EpisodeScrapedData data, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO episodes (id, series_id, episode_number, title, aired_at, is_published, created_at)
            VALUES (gen_random_uuid(), {data.SeriesId}, {data.EpisodeNumber}, {data.Title}, {data.AiredAt}, true, now())
            ON CONFLICT (series_id, episode_number) DO UPDATE SET
                title        = COALESCE(EXCLUDED.title, episodes.title),
                aired_at     = COALESCE(EXCLUDED.aired_at, episodes.aired_at),
                is_published = true
            """, ct);

        return await db.Episodes
            .Where(e => e.SeriesId == data.SeriesId && e.EpisodeNumber == data.EpisodeNumber)
            .Select(e => e.Id)
            .SingleAsync(ct);
    }

    /// <summary>
    /// Upserts a mirror. Conflict key: (episode_id, embed_url).
    /// Resets consecutive_failures to 0 and marks is_active = true on re-discovery.
    /// </summary>
    public async Task UpsertMirrorAsync(MirrorScrapedData data, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO mirrors
                (id, episode_id, provider_name, embed_url, quality_label, priority, is_active,
                 consecutive_failures, created_at)
            VALUES (gen_random_uuid(), {data.EpisodeId}, {data.ProviderName}, {data.EmbedUrl}, {data.QualityLabel}, {data.Priority}, true, 0, now())
            ON CONFLICT (episode_id, embed_url) DO UPDATE SET
                provider_name        = EXCLUDED.provider_name,
                quality_label        = EXCLUDED.quality_label,
                priority             = EXCLUDED.priority,
                is_active            = true,
                consecutive_failures = 0,
                last_checked_at      = now()
            """, ct);
    }
}
