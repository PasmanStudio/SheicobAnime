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
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO series (id, slug, title, cover_url, status, type, created_at, updated_at)
            VALUES (gen_random_uuid(), {0}, {1}, {2}, {3}, {4}, now(), now())
            ON CONFLICT (slug) DO UPDATE SET
                title        = EXCLUDED.title,
                cover_url    = COALESCE(EXCLUDED.cover_url, series.cover_url),
                status       = COALESCE(EXCLUDED.status, series.status),
                type         = COALESCE(EXCLUDED.type, series.type),
                updated_at   = now(),
                last_scraped_at = now()
            """,
            [data.Slug, data.Title, data.CoverUrl, data.Status, data.Type],
            ct);

        return await db.Series
            .Where(s => s.Slug == data.Slug)
            .Select(s => s.Id)
            .SingleAsync(ct);
    }

    /// <summary>
    /// Upserts an episode. Conflict key: (series_id, episode_number).
    /// Returns the episode UUID.
    /// </summary>
    public async Task<Guid> UpsertEpisodeAsync(EpisodeScrapedData data, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO episodes (id, series_id, episode_number, title, is_published, created_at)
            VALUES (gen_random_uuid(), {0}, {1}, {2}, true, now())
            ON CONFLICT (series_id, episode_number) DO UPDATE SET
                title        = COALESCE(EXCLUDED.title, episodes.title),
                is_published = true
            """,
            [data.SeriesId, data.EpisodeNumber, data.Title],
            ct);

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
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO mirrors
                (id, episode_id, provider_name, embed_url, quality_label, priority, is_active,
                 consecutive_failures, created_at)
            VALUES (gen_random_uuid(), {0}, {1}, {2}, {3}, {4}, true, 0, now())
            ON CONFLICT (episode_id, embed_url) DO UPDATE SET
                provider_name        = EXCLUDED.provider_name,
                quality_label        = EXCLUDED.quality_label,
                priority             = EXCLUDED.priority,
                is_active            = true,
                consecutive_failures = 0,
                last_checked_at      = now()
            """,
            [data.EpisodeId, data.ProviderName, data.EmbedUrl, data.QualityLabel, data.Priority],
            ct);
    }
}
