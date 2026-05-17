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

    private static JkAnimeHttpClient CreateHttpClient() =>
        new(new StubHttpClientFactory(), NullLogger<JkAnimeHttpClient>.Instance);

    // ── Strategy identity ──────────────────────────────────

    [Fact]
    public void Source2Strategy_SourceKey_IsSource2()
    {
        var db = CreateInMemoryDb();
        var config = CreateConfig(new() { ["Source2:BaseUrl"] = "https://example.com" });
        var probe = new MirrorProbeService(new StubHttpClientFactory());
        var upsert = new UpsertPipelineService(db);
        var http = CreateHttpClient();
        var strategy = new Source2Strategy(db, upsert, http, null, null, config, NullLogger<Source2Strategy>.Instance);

        Assert.Equal("source2", strategy.SourceKey);
    }

    [Fact]
    public void Source2Strategy_Implements_IScrapeStrategy()
    {
        var db = CreateInMemoryDb();
        var config = CreateConfig(new() { ["Source2:BaseUrl"] = "https://example.com" });
        var probe = new MirrorProbeService(new StubHttpClientFactory());
        var upsert = new UpsertPipelineService(db);
        var http = CreateHttpClient();

        IScrapeStrategy s2 = new Source2Strategy(db, upsert, http, null, null, config, NullLogger<Source2Strategy>.Instance);

        Assert.Equal("source2", s2.SourceKey);
    }

    // ── Blocked slug guard ─────────────────────────────────

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
        var http = CreateHttpClient();
        var strategy = new Source2Strategy(db, upsert, http, null, null, config, NullLogger<Source2Strategy>.Instance);

        var result = await strategy.ScrapeAsync(job.Id);

        Assert.False(result.Success);
        Assert.Contains("blocked_slugs", result.ErrorMessage);
    }

    // ── Missing job guard ───────────────────────

    [Fact]
    public async Task Source2_ReturnsFailure_WhenJobNotFound()
    {
        var db = CreateInMemoryDb();
        var config = CreateConfig(new() { ["Source2:BaseUrl"] = "https://example.com" });
        var probe = new MirrorProbeService(new StubHttpClientFactory());
        var upsert = new UpsertPipelineService(db);
        var http = CreateHttpClient();
        var strategy = new Source2Strategy(db, upsert, http, null, null, config, NullLogger<Source2Strategy>.Instance);

        var result = await strategy.ScrapeAsync(Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage);
    }

    // ── Provider name extraction ───────────────────────────

    [Theory]
    [InlineData("https://sfastwish.com/e/abc123", "streamwish")]
    [InlineData("https://filemoon.sx/e/xyz", "filemoon")]
    [InlineData("https://ok.ru/videoembed/12345", "okru")]
    [InlineData("https://mp4upload.com/embed-abc", "mp4upload")]
    [InlineData("https://www.yourupload.com/embed/xyz", "yourupload")]
    [InlineData("https://voe.sx/e/abc123", "voe")]
    [InlineData("https://vidhide.com/e/abc123", "vidhide")]
    [InlineData("https://dsvplay.com/e/abc123", "vidhide")]
    [InlineData("https://mxdrop.org/d/abc", "mixdrop")]
    [InlineData("https://bysekoze.com/e/abc", "streamwish")]
    [InlineData("https://mixdrop.co/e/abc", "mixdrop")]
    [InlineData("https://mixdrop.top/e/xwlpj114c87oq4", "mixdrop")]
    public void ExtractProviderName_MapsCorrectly(string url, string expected)
    {
        Assert.Equal(expected, Source2Strategy.ExtractProviderName(url));
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
