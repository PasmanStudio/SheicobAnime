using System.Net;
using System.Net.Http.Json;

namespace AnimeIndex.Api.Tests;

public class AdminTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task AdminEndpoints_Return401WithoutKey()
    {
        var response = await _client.GetAsync("/admin/scrape-jobs");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoints_Return401WithWrongKey()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/scrape-jobs");
        request.Headers.Add("X-Admin-Key", "wrong-key");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoints_Return200WithCorrectKey()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/scrape-jobs");
        request.Headers.Add("X-Admin-Key", "test-admin-key");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
