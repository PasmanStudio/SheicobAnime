using System.Net;
using System.Net.Http.Json;

namespace AnimeIndex.Api.Tests;

public class GenreTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task GetGenres_ReturnsOk()
    {
        var response = await _client.GetAsync("/genres");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
