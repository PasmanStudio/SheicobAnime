using System.Net;
using System.Net.Http.Json;
using AnimeIndex.Api.Data;
using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Api.DTOs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AnimeIndex.Api.Tests;

public class EpisodeSitemapTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public EpisodeSitemapTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetEpisodeSitemap_ReturnsEmptyWhenNoData()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/episodes/sitemap");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content
            .ReadFromJsonAsync<PaginatedResponse<EpisodeSitemapDto>>();
        Assert.NotNull(body);
        Assert.Equal(0, body.Total);
        Assert.Empty(body.Data);
    }

    [Fact]
    public async Task GetEpisodeSitemap_ReturnsOnlyPublishedEpisodesPaginated()
    {
        var seededFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<AppDbContext>();

                var dbName = $"SitemapTestDb_{Guid.NewGuid()}";
                services.AddDbContext<AppDbContext>(options =>
                    options.UseInMemoryDatabase(dbName));

                var sp = services.BuildServiceProvider();
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();

                var series = new Series
                {
                    Id = Guid.NewGuid(),
                    Slug = "sitemap-series",
                    Title = "Sitemap Series",
                    Year = 2024,
                    Status = "ongoing",
                    Type = "tv",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.Series.Add(series);

                for (short i = 1; i <= 3; i++)
                {
                    db.Episodes.Add(new Episode
                    {
                        Id = Guid.NewGuid(),
                        SeriesId = series.Id,
                        EpisodeNumber = i,
                        // El episodio 3 no está publicado — no debe aparecer
                        IsPublished = i < 3,
                        CreatedAt = DateTime.UtcNow.AddDays(-10 + i)
                    });
                }
                db.SaveChanges();
            });
        });

        var client = seededFactory.CreateClient();

        var response = await client.GetAsync("/episodes/sitemap");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content
            .ReadFromJsonAsync<PaginatedResponse<EpisodeSitemapDto>>();
        Assert.NotNull(body);
        Assert.Equal(2, body.Total);
        Assert.All(body.Data, e => Assert.Equal("sitemap-series", e.SeriesSlug));
        // Orden estable por CreatedAt: primero el episodio 1
        Assert.Equal(1, body.Data[0].EpisodeNumber);
        Assert.Equal(2, body.Data[1].EpisodeNumber);

        // Paginación: página 2 con pageSize 1 → solo el episodio 2
        var page2 = await client.GetFromJsonAsync<PaginatedResponse<EpisodeSitemapDto>>(
            "/episodes/sitemap?page=2&pageSize=1");
        Assert.NotNull(page2);
        Assert.Equal(2, page2.Total);
        Assert.Single(page2.Data);
        Assert.Equal(2, page2.Data[0].EpisodeNumber);
    }
}
