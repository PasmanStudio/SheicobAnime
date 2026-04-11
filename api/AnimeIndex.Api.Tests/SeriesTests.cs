using System.Net;
using System.Net.Http.Json;
using AnimeIndex.Api.Data;
using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Api.DTOs;
using AnimeIndex.Api.Infrastructure.Cache;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AnimeIndex.Api.Tests;

public class SeriesTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly TestWebApplicationFactory _factory;

    public SeriesTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetSeries_ReturnsEmptyWhenNoData()
    {
        var response = await _client.GetAsync("/series");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<PaginatedResponse<SeriesDto>>();
        Assert.NotNull(body);
        Assert.Equal(0, body.Total);
    }

    [Fact]
    public async Task GetSeriesBySlug_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/series/nonexistent-slug");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetSeries_ReturnsSeedDataAfterInsert()
    {
        // Create a separate factory with pre-seeded data to avoid InMemory DB isolation issues
        var seededFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<AppDbContext>();

                var dbName = $"SeededTestDb_{Guid.NewGuid()}";
                services.AddDbContext<AppDbContext>(options =>
                    options.UseInMemoryDatabase(dbName));

                // Seed data after building
                var sp = services.BuildServiceProvider();
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();

                db.Series.Add(new Series
                {
                    Id = Guid.NewGuid(),
                    Slug = "test-series-seeded",
                    Title = "Test Series Seeded",
                    Year = 2024,
                    Status = "ongoing",
                    Type = "tv",
                    Score = 8.0m,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
                db.SaveChanges();
            });
        });

        var client = seededFactory.CreateClient();
        var response = await client.GetAsync("/series/test-series-seeded");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SeriesDto>();
        Assert.NotNull(body);
        Assert.Equal("test-series-seeded", body.Slug);
        Assert.Equal("Test Series Seeded", body.Title);
    }
}
