using System.Net;
using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Api.Infrastructure.Resolvers;
using Xunit;

namespace AnimeIndex.Api.Tests;

public class HosterResolversTests
{
    private static IHttpClientFactory MakeFactory(string responseHtml, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHandler(responseHtml, status);
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        return new SingleClientFactory(client);
    }

    private static Mirror MakeMirror(string providerName, string embedUrl, short quality = 720) => new()
    {
        Id = Guid.NewGuid(),
        EpisodeId = Guid.NewGuid(),
        ProviderName = providerName,
        EmbedUrl = embedUrl,
        QualityLabel = quality,
        Priority = 1,
        IsActive = true
    };

    [Fact]
    public async Task Mp4Upload_ExtractsMp4UrlFromEmbedHtml()
    {
        // Shape mirrors the real production markup: player.src({ type, src }) — see
        // ResolverFixtureTests for a test that runs against an actual captured dump.
        var html = """
        <html><body><script>
            player.src({
                type: "video/mp4",
                src: "https://www4.mp4upload.com/d/abc123/video.mp4"
            });
        </script></body></html>
        """;
        var resolver = new Mp4UploadResolver(MakeFactory(html));
        var mirror = MakeMirror("mp4upload", "https://www.mp4upload.com/embed-abc123.html");

        var result = await resolver.ResolveAsync(mirror);

        Assert.Equal(SourceFormat.Mp4, result.Format);
        Assert.Equal("https://www4.mp4upload.com/d/abc123/video.mp4", result.Url);
        Assert.False(result.ProxyRequired);
        Assert.NotNull(result.Headers);
        Assert.Contains("Referer", result.Headers!.Keys);
    }

    [Fact]
    public async Task Mp4Upload_ThrowsWhenIdMissing()
    {
        var resolver = new Mp4UploadResolver(MakeFactory(""));
        var mirror = MakeMirror("mp4upload", "https://example.com/notmp4upload");
        var ex = await Assert.ThrowsAsync<ResolverException>(() => resolver.ResolveAsync(mirror));
        Assert.Equal(ResolverFailureReason.PatternChanged, ex.Reason);
    }

    [Fact]
    public async Task Mp4Upload_ThrowsOn404()
    {
        var resolver = new Mp4UploadResolver(MakeFactory("not found", HttpStatusCode.NotFound));
        var mirror = MakeMirror("mp4upload", "https://www.mp4upload.com/embed-xyz.html");
        var ex = await Assert.ThrowsAsync<ResolverException>(() => resolver.ResolveAsync(mirror));
        Assert.Equal(ResolverFailureReason.EmbedUnavailable, ex.Reason);
    }

    [Fact]
    public async Task Vidhide_ExtractsM3u8FromEmbed()
    {
        var html = """
        <html><body><script>
            sources: [{file:"https://cdn.vidhide.com/hls/abc/master.m3u8?token=xyz", type:"hls"}]
        </script></body></html>
        """;
        var resolver = new VidhideResolver(MakeFactory(html));
        var mirror = MakeMirror("vidhide", "https://vidhide.com/e/abc123");

        var result = await resolver.ResolveAsync(mirror);

        Assert.Equal(SourceFormat.Hls, result.Format);
        Assert.StartsWith("https://cdn.vidhide.com/hls/abc/master.m3u8", result.Url);
    }

    [Fact]
    public async Task Streamwish_ExtractsM3u8AndSetsProxyRequired()
    {
        var html = """
        <html><body><script>
            jwplayer("video").setup({sources:[{file:"https://stream.streamwish.com/hls/abc/index.m3u8?t=xyz", type:"hls"}]});
        </script></body></html>
        """;
        var resolver = new StreamwishResolver(MakeFactory(html));
        var mirror = MakeMirror("streamwish", "https://streamwish.com/e/abc");

        var result = await resolver.ResolveAsync(mirror);

        Assert.Equal(SourceFormat.Hls, result.Format);
        Assert.True(result.ProxyRequired);
        Assert.Contains("Referer", result.Headers!.Keys);
    }

    [Fact]
    public async Task Okru_ExtractsBestQualityFromMetadata()
    {
        // OK.ru embeds the player config in data-options as HTML-encoded JSON.
        // The metadata field inside flashvars is itself a JSON-encoded string.
        var metadataInner = "{\"videos\":[" +
            "{\"name\":\"sd\",\"url\":\"https://vk.com/sd.mp4\"}," +
            "{\"name\":\"hd\",\"url\":\"https://vk.com/hd.mp4\"}," +
            "{\"name\":\"low\",\"url\":\"https://vk.com/low.mp4\"}" +
            "]}";
        var dataOptionsJson = "{\"flashvars\":{\"metadata\":" +
            System.Text.Json.JsonSerializer.Serialize(metadataInner) + "}}";
        var html = "<html><body><div id=\"v\" data-options=\"" +
            System.Net.WebUtility.HtmlEncode(dataOptionsJson) + "\"></div></body></html>";

        var resolver = new OkruResolver(MakeFactory(html));
        var mirror = MakeMirror("okru", "https://ok.ru/videoembed/12345");

        var result = await resolver.ResolveAsync(mirror);

        // Should pick HD (720p) — highest mapped quality
        Assert.Equal("https://vk.com/hd.mp4", result.Url);
        Assert.Equal(SourceFormat.Mp4, result.Format);
        Assert.NotNull(result.Qualities);
        Assert.Equal(3, result.Qualities!.Count);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;
        public StubHandler(string body, HttpStatusCode status) { _body = body; _status = status; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient c) { _client = c; }
        public HttpClient CreateClient(string name) => _client;
    }
}
