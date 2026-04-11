using System.Net;
using System.Net.Http.Json;

namespace AnimeIndex.Api.Tests;

public class HealthTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<HealthBody>();
        Assert.NotNull(body);
        Assert.Equal("healthy", body.Status);
        Assert.Equal("ok", body.Db);
        Assert.Equal("ok", body.Cache);
    }

    private record HealthBody(string Status, string Db, string Cache, string Version);
}
