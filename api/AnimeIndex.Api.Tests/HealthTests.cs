using System.Net;
using System.Net.Http.Json;

namespace AnimeIndex.Api.Tests;

public class HealthTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Health_IsLiveness_AndDoesNotTouchDependencies()
    {
        // /health es el healthCheckPath de Render: debe responder 200 mientras el
        // proceso viva, sin consultar DB ni cache. Si volviera a depender de la DB,
        // una saturación haría que Render reinicie la instancia en bucle.
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<LivenessBody>();
        Assert.NotNull(body);
        Assert.Equal("alive", body.Status);
        Assert.False(string.IsNullOrWhiteSpace(body.Version));
    }

    [Fact]
    public async Task HealthReady_ReportsDependencies()
    {
        var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ReadyBody>();
        Assert.NotNull(body);
        Assert.Equal("healthy", body.Status);
        Assert.Equal("ok", body.Db);
        Assert.Equal("ok", body.Cache);
    }

    private record LivenessBody(string Status, string Version);

    private record ReadyBody(string Status, string Db, string Cache, string Version, long? DbMs = null, long? CacheMs = null, long? TotalMs = null);
}
