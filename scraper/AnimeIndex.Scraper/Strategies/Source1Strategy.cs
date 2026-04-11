using AnimeIndex.Api.Data;
using AnimeIndex.Api.Infrastructure.Scraping;
using AnimeIndex.Scraper.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Strategies;

/// <summary>
/// First concrete scrape strategy. Reads Source1:BaseUrl from config,
/// discovers series/episodes, probes each mirror URL for embeddability,
/// then persists via UpsertPipelineService.
/// </summary>
public sealed class Source1Strategy(
    AppDbContext db,
    MirrorProbeService probe,
    UpsertPipelineService upsert,
    IConfiguration config,
    ILogger<Source1Strategy> logger) : PlaywrightBase, IScrapeStrategy
{
    public string SourceKey => "source1";

    public async Task<ScrapeResult> ScrapeAsync(Guid scrapeJobId, CancellationToken ct = default)
    {
        var baseUrl = config["Source1:BaseUrl"]
            ?? throw new InvalidOperationException("Source1:BaseUrl is not configured.");
        var delayMs = config.GetValue<int>("Source1:DelayMs", 1500);

        var seriesCount = 0;
        var episodeCount = 0;
        var mirrorCount = 0;

        // ── Check blocked_slugs for this job's series ────────
        var job = await db.ScrapeJobs
            .Include(j => j.Series)
            .FirstOrDefaultAsync(j => j.Id == scrapeJobId, ct);

        if (job is null)
            return new ScrapeResult(false, $"ScrapeJob {scrapeJobId} not found.");

        string? targetSlug = job.Series?.Slug;
        if (targetSlug is not null)
        {
            var isBlocked = await db.BlockedSlugs
                .AnyAsync(b => b.Slug == targetSlug, ct);
            if (isBlocked)
            {
                logger.LogWarning("Slug {Slug} is blocked — skipping", targetSlug);
                return new ScrapeResult(false, $"Slug '{targetSlug}' is in blocked_slugs.");
            }
        }

        await using var _ = this;
        await InitializeAsync();

        logger.LogInformation("Source1Strategy starting — baseUrl={BaseUrl}", baseUrl);

        // ── Discover series list ──────────────────────────────
        var discovered = await DiscoverSeriesAsync(baseUrl, ct);

        foreach (var series in discovered)
        {
            if (ct.IsCancellationRequested) break;

            // Re-check blocked slug for each discovered series
            var blocked = await db.BlockedSlugs.AnyAsync(b => b.Slug == series.Slug, ct);
            if (blocked)
            {
                logger.LogDebug("Skipping blocked slug {Slug}", series.Slug);
                continue;
            }

            var seriesId = await upsert.UpsertSeriesAsync(series, ct);
            seriesCount++;

            // ── Discover episodes for this series ─────────────
            var episodesUrl = $"{baseUrl.TrimEnd('/')}/{series.Slug}";
            if (!await GoToAsync(episodesUrl, ct))
            {
                logger.LogWarning("Failed to navigate to {Url}", episodesUrl);
                continue;
            }

            var episodes = await ScrapeEpisodesAsync(seriesId, ct);

            foreach (var ep in episodes)
            {
                if (ct.IsCancellationRequested) break;

                var episodeId = await upsert.UpsertEpisodeAsync(ep, ct);
                episodeCount++;

                foreach (var mirror in ep.PendingMirrors)
                {
                    var embeddable = await probe.IsEmbeddableAsync(mirror.EmbedUrl, ct);
                    if (!embeddable)
                    {
                        logger.LogDebug("Mirror {Url} is not embeddable — skipped", mirror.EmbedUrl);
                        continue;
                    }

                    await upsert.UpsertMirrorAsync(mirror with { EpisodeId = episodeId }, ct);
                    mirrorCount++;
                }

                await Task.Delay(delayMs, ct);
            }
        }

        logger.LogInformation(
            "Source1Strategy complete — series={S} episodes={E} mirrors={M}",
            seriesCount, episodeCount, mirrorCount);

        return new ScrapeResult(true,
            SeriesIndexed: seriesCount,
            EpisodesIndexed: episodeCount,
            MirrorsIndexed: mirrorCount);
    }

    // ── Private page-scraping helpers ─────────────────────────

    private async Task<IReadOnlyList<SeriesScrapedData>> DiscoverSeriesAsync(
        string baseUrl, CancellationToken ct)
    {
        if (!await GoToAsync(baseUrl, ct))
        {
            logger.LogError("Cannot reach {BaseUrl}", baseUrl);
            return [];
        }

        // Select all series links — adjust selectors per real source
        var titles = await Page.Locator("a[data-series-slug]").AllAsync();
        var result = new List<SeriesScrapedData>();

        foreach (var el in titles)
        {
            var slug = await el.GetAttributeAsync("data-series-slug");
            var title = await el.InnerTextAsync();
            if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(title))
                continue;

            result.Add(new SeriesScrapedData(
                Slug: slug.Trim(),
                Title: title.Trim(),
                CoverUrl: null,
                Status: "ongoing",
                Type: "tv"));
        }

        return result;
    }

    private async Task<IReadOnlyList<EpisodeScrapedData>> ScrapeEpisodesAsync(
        Guid seriesId, CancellationToken ct)
    {
        // Adjust selectors per real source
        var rows = await Page.Locator("li[data-ep-number]").AllAsync();
        var result = new List<EpisodeScrapedData>();

        foreach (var row in rows)
        {
            var epNumStr = await row.GetAttributeAsync("data-ep-number");
            var embedUrl = await row.GetAttributeAsync("data-embed-url");

            if (!short.TryParse(epNumStr, out var epNum) ||
                string.IsNullOrWhiteSpace(embedUrl)) continue;

            var title = await row.GetAttributeAsync("data-ep-title");
            var mirrors = new List<MirrorScrapedData>
            {
                new(EpisodeId: Guid.Empty, ProviderName: SourceKey,
                    EmbedUrl: embedUrl, QualityLabel: 720, Priority: 0)
            };

            result.Add(new EpisodeScrapedData(
                SeriesId: seriesId,
                EpisodeNumber: epNum,
                Title: title,
                PendingMirrors: mirrors));
        }

        return result;
    }
}
