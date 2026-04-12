using AnimeIndex.Api.Data;
using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Api.Infrastructure.Scraping;
using AnimeIndex.Scraper;
using AnimeIndex.Scraper.Infrastructure;
using AnimeIndex.Scraper.Strategies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnimeIndex.Api.Tests;

public class ScraperTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ScraperTestDb_{Guid.NewGuid()}")
            .Options;
        return new AppDbContext(options);
    }

    private static IConfiguration CreateConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    // ── Strategy identity ──────────────────────────────────

    [Fact]
    public void Source1Strategy_SourceKey_IsSource1()
    {
        var db = CreateInMemoryDb();
        var config = CreateConfig(new() { ["Source1:BaseUrl"] = "https://example.com" });
        var probe = new MirrorProbeService(new StubHttpClientFactory());
        var upsert = new UpsertPipelineService(db);
        var strategy = new Source1Strategy(db, probe, upsert, config, NullLogger<Source1Strategy>.Instance);

        Assert.Equal("source1", strategy.SourceKey);
    }

    [Fact]
    public void Source2Strategy_SourceKey_IsSource2()
    {
        var db = CreateInMemoryDb();
        var config = CreateConfig(new() { ["Source2:BaseUrl"] = "https://example.com" });
        var probe = new MirrorProbeService(new StubHttpClientFactory());
        var upsert = new UpsertPipelineService(db);
        var strategy = new Source2Strategy(db, probe, upsert, config, NullLogger<Source2Strategy>.Instance);

        Assert.Equal("source2", strategy.SourceKey);
    }

    [Fact]
    public void BothStrategies_Implement_IScrapeStrategy()
    {
        var db = CreateInMemoryDb();
        var config = CreateConfig(new()
        {
            ["Source1:BaseUrl"] = "https://example.com",
            ["Source2:BaseUrl"] = "https://example.com"
        });
        var probe = new MirrorProbeService(new StubHttpClientFactory());
        var upsert = new UpsertPipelineService(db);

        IScrapeStrategy s1 = new Source1Strategy(db, probe, upsert, config, NullLogger<Source1Strategy>.Instance);
        IScrapeStrategy s2 = new Source2Strategy(db, probe, upsert, config, NullLogger<Source2Strategy>.Instance);

        Assert.NotEqual(s1.SourceKey, s2.SourceKey);
    }

    // ── Blocked slug guard ─────────────────────────────────

    [Fact]
    public async Task Source1_RejectsBlockedSlug()
    {
        var db = CreateInMemoryDb();
        var series = new Series { Id = Guid.NewGuid(), Slug = "blocked-anime", Title = "Blocked" };
        db.Series.Add(series);
        db.BlockedSlugs.Add(new BlockedSlug { Slug = "blocked-anime", Reason = "DMCA" });
        var job = new ScrapeJob
        {
            Id = Guid.NewGuid(),
            SeriesId = series.Id,
            JobType = "scrape:source1",
            Status = "pending",
            ScheduledAt = DateTime.UtcNow
        };
        db.ScrapeJobs.Add(job);
        await db.SaveChangesAsync();

        var config = CreateConfig(new() { ["Source1:BaseUrl"] = "https://example.com" });
        var probe = new MirrorProbeService(new StubHttpClientFactory());
        var upsert = new UpsertPipelineService(db);
        var strategy = new Source1Strategy(db, probe, upsert, config, NullLogger<Source1Strategy>.Instance);

        var result = await strategy.ScrapeAsync(job.Id);

        Assert.False(result.Success);
        Assert.Contains("blocked_slugs", result.ErrorMessage);
    }

    [Fact]
    public async Task Source2_RejectsBlockedSlug()
    {
        var db = CreateInMemoryDb();
        var series = new Series { Id = Guid.NewGuid(), Slug = "blocked-jk", Title = "Blocked JK" };
        db.Series.Add(series);
        db.BlockedSlugs.Add(new BlockedSlug { Slug = "blocked-jk", Reason = "DMCA" });
        var job = new ScrapeJob
        {
            Id = Guid.NewGuid(),
            SeriesId = series.Id,
            JobType = "scrape:source2",
            Status = "pending",
            ScheduledAt = DateTime.UtcNow
        };
        db.ScrapeJobs.Add(job);
        await db.SaveChangesAsync();

        var config = CreateConfig(new() { ["Source2:BaseUrl"] = "https://example.com" });
        var probe = new MirrorProbeService(new StubHttpClientFactory());
        var upsert = new UpsertPipelineService(db);
        var strategy = new Source2Strategy(db, probe, upsert, config, NullLogger<Source2Strategy>.Instance);

        var result = await strategy.ScrapeAsync(job.Id);

        Assert.False(result.Success);
        Assert.Contains("blocked_slugs", result.ErrorMessage);
    }

    // ── Missing job guard ──────────────────────────────────

    [Fact]
    public async Task Source1_ReturnsFailure_WhenJobNotFound()
    {
        var db = CreateInMemoryDb();
        var config = CreateConfig(new() { ["Source1:BaseUrl"] = "https://example.com" });
        var probe = new MirrorProbeService(new StubHttpClientFactory());
        var upsert = new UpsertPipelineService(db);
        var strategy = new Source1Strategy(db, probe, upsert, config, NullLogger<Source1Strategy>.Instance);

        var result = await strategy.ScrapeAsync(Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage);
    }

    [Fact]
    public async Task Source2_ReturnsFailure_WhenJobNotFound()
    {
        var db = CreateInMemoryDb();
        var config = CreateConfig(new() { ["Source2:BaseUrl"] = "https://example.com" });
        var probe = new MirrorProbeService(new StubHttpClientFactory());
        var upsert = new UpsertPipelineService(db);
        var strategy = new Source2Strategy(db, probe, upsert, config, NullLogger<Source2Strategy>.Instance);

        var result = await strategy.ScrapeAsync(Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage);
    }

    // ── Config validation ──────────────────────────────────

    [Fact]
    public async Task Source1_ThrowsWithoutBaseUrl()
    {
        var db = CreateInMemoryDb();
        var config = CreateConfig(new() { ["Source1:DelayMs"] = "2000" }); // no BaseUrl
        var probe = new MirrorProbeService(new StubHttpClientFactory());
        var upsert = new UpsertPipelineService(db);

        db.ScrapeJobs.Add(new ScrapeJob
        {
            Id = Guid.NewGuid(),
            JobType = "scrape:source1",
            Status = "pending",
            ScheduledAt = DateTime.UtcNow
        });
        db.SaveChanges();
        var jobId = db.ScrapeJobs.First().Id;

        var strategy = new Source1Strategy(db, probe, upsert, config, NullLogger<Source1Strategy>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => strategy.ScrapeAsync(jobId));
    }

    // ── ScrapeResult record ────────────────────────────────

    [Fact]
    public void ScrapeResult_DefaultsToZeroCounts()
    {
        var result = new ScrapeResult(true);
        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(0, result.SeriesIndexed);
        Assert.Equal(0, result.EpisodesIndexed);
        Assert.Equal(0, result.MirrorsIndexed);
    }

    // ── Data model records ─────────────────────────────────

    [Fact]
    public void SeriesScrapedData_SupportsAllNewFields()
    {
        var data = new SeriesScrapedData(
            Slug: "test",
            Title: "Test",
            CoverUrl: "http://img.jpg",
            Status: "ongoing",
            Type: "tv",
            Synopsis: "A great show",
            TitleRomaji: "Tesuto",
            TitleNative: "テスト",
            Score: 8.5m,
            Year: 2024,
            EpisodeCount: 24,
            Genres: ["Action", "Comedy"]);

        Assert.Equal("test", data.Slug);
        Assert.Equal("A great show", data.Synopsis);
        Assert.Equal(8.5m, data.Score);
        Assert.Equal(2, data.Genres!.Count);
    }

    [Fact]
    public void EpisodeScrapedData_SupportsAiredAt()
    {
        var aired = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var ep = new EpisodeScrapedData(
            SeriesId: Guid.NewGuid(),
            EpisodeNumber: 1,
            Title: "Episode 1",
            PendingMirrors: [],
            AiredAt: aired);

        Assert.Equal(aired, ep.AiredAt);
    }

    [Fact]
    public void UpsertPipelineService_HasSyncEpisodeCountMethod()
    {
        var db = CreateInMemoryDb();
        var service = new UpsertPipelineService(db);
        var method = service.GetType().GetMethod("SyncEpisodeCountAsync");

        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method.ReturnType);
    }

    [Fact]
    public void SeriesScrapedData_YearCanBeSet()
    {
        var data = new SeriesScrapedData(
            Slug: "test-year",
            Title: "Test Year",
            CoverUrl: null,
            Status: "ongoing",
            Type: "tv",
            Year: 2023);

        Assert.Equal((short)2023, data.Year);
    }

    // ── Stub for IHttpClientFactory ────────────────────────

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
