using System.Net;
using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Api.Infrastructure.Resolvers;
using Xunit;

namespace AnimeIndex.Api.Tests;

/// <summary>
/// Regression tests that pin resolver extraction against real production HTML dumps
/// (captured 2026-04-18 from the Re:Zero S4E2 mirrors on sheicobanime.vercel.app).
/// These guard against silent regressions when we update regex/URL-building logic.
/// If a hoster changes their page shape in prod, these tests will keep passing until
/// we refresh the fixtures AND update the resolver — a matching pair of changes.
/// </summary>
public class ResolverFixtureTests
{
    private static string LoadFixture(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Resolvers", filename);
        Assert.True(File.Exists(path), $"Fixture not found: {path}");
        return File.ReadAllText(path);
    }

    private static IHttpClientFactory FixtureFactory(string filename)
    {
        var body = LoadFixture(filename);
        var handler = new FixtureHandler(body);
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        return new FixtureFactoryImpl(client);
    }

    private static Mirror MakeMirror(string providerName, string embedUrl) => new()
    {
        Id = Guid.NewGuid(),
        EpisodeId = Guid.NewGuid(),
        ProviderName = providerName,
        EmbedUrl = embedUrl,
        QualityLabel = 720,
        Priority = 1,
        IsActive = true,
    };

    [Fact]
    public async Task Streamwish_ExtractsM3u8FromRealSfastwishDump()
    {
        // Real HTML captured from https://sfastwish.com/e/c2fl307yw4rg
        var resolver = new StreamwishResolver(FixtureFactory("streamwish-sfastwish.html"));
        var mirror = MakeMirror("streamwish", "https://sfastwish.com/e/c2fl307yw4rg");

        var result = await resolver.ResolveAsync(mirror);

        Assert.Equal(SourceFormat.Hls, result.Format);
        Assert.EndsWith(".m3u8", result.Url.Split('?')[0]);
        Assert.Contains("premilkyway.com", result.Url);
        Assert.Contains("master.m3u8", result.Url);
        Assert.True(result.ProxyRequired, "Streamwish tokens are IP-bound so proxy must be required");
        Assert.NotNull(result.Headers);
        Assert.Equal("https://sfastwish.com/", result.Headers!["Referer"]);
    }

    [Fact]
    public async Task Vidhide_ExtractsM3u8FromRealVidhidevipDump()
    {
        // Real HTML captured from https://vidhidevip.com/embed/o3zpuy8qksv6
        // The original VidhideResolver built /embed-{id}.html which returned 404.
        // After fix: /embed/{id} + EmbedIdRegex accepts the new /embed/ prefix.
        var resolver = new VidhideResolver(FixtureFactory("vidhide-vidhidevip.html"));
        var mirror = MakeMirror("vidhide", "https://vidhidevip.com/embed/o3zpuy8qksv6");

        var result = await resolver.ResolveAsync(mirror);

        Assert.Equal(SourceFormat.Hls, result.Format);
        Assert.EndsWith(".m3u8", result.Url.Split('?')[0]);
        Assert.Contains("acek-cdn.com", result.Url);
        Assert.Contains("master.m3u8", result.Url);
        Assert.NotNull(result.Headers);
        Assert.Equal("https://vidhidevip.com/", result.Headers!["Referer"]);
    }

    [Fact]
    public async Task Mp4Upload_ExtractsRealMp4UrlNotVideojsLibrary()
    {
        // Real HTML captured from https://www.mp4upload.com/embed-vmr4f84e9pz0.html
        // The original Mp4UploadResolver regex was too permissive and returned
        // https://www.mp4upload.com/player/videojs/video.min.js (the JS library!).
        // After fix: regex anchored to `player.src({ ... src: "..." })`.
        var resolver = new Mp4UploadResolver(FixtureFactory("mp4upload.html"));
        var mirror = MakeMirror("mp4upload", "https://www.mp4upload.com/embed-vmr4f84e9pz0.html");

        var result = await resolver.ResolveAsync(mirror);

        Assert.Equal(SourceFormat.Mp4, result.Format);
        Assert.DoesNotContain("videojs", result.Url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/player/", result.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("video.mp4", result.Url);
        Assert.Contains("mp4upload.com", result.Url);
        Assert.NotNull(result.Headers);
        Assert.Equal("https://www.mp4upload.com/", result.Headers!["Referer"]);
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly string _body;
        public FixtureHandler(string body) { _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "text/html"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class FixtureFactoryImpl : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public FixtureFactoryImpl(HttpClient c) { _client = c; }
        public HttpClient CreateClient(string name) => _client;
    }
}
