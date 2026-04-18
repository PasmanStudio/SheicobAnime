using AnimeIndex.Api.Infrastructure.Resolvers;
using Xunit;

namespace AnimeIndex.Api.Tests;

public class ResolverRegistryTests
{
    [Fact]
    public void Supports_ReturnsTrueForKnownHoster()
    {
        var fake = new FakeResolver("mp4upload");
        var registry = new ResolverRegistry(new[] { (IHosterResolver)fake });

        Assert.True(registry.Supports("mp4upload"));
        Assert.True(registry.Supports("MP4Upload"));
        Assert.True(registry.Supports("Mp4-Upload"));   // alias
        Assert.False(registry.Supports("randomprovider"));
        Assert.False(registry.Supports(null));
        Assert.False(registry.Supports(""));
    }

    [Theory]
    [InlineData("streamwsh", "streamwish")]
    [InlineData("STREAMWISH", "streamwish")]
    [InlineData("vid-hide", "vidhide")]
    [InlineData("ok.ru", "okru")]
    [InlineData("Voe.SX", "voe")]
    [InlineData("MIXDROP", "mixdrop")]
    public void NormalizeHosterName_AppliesCommonAliases(string input, string expected)
    {
        Assert.Equal(expected, ResolverRegistry.NormalizeHosterName(input));
    }

    [Fact]
    public void GetFor_ReturnsRegisteredResolver()
    {
        var swish = new FakeResolver("streamwish");
        var ok = new FakeResolver("okru");
        var registry = new ResolverRegistry(new IHosterResolver[] { swish, ok });

        Assert.Same(swish, registry.GetFor("Streamwish"));
        Assert.Same(ok, registry.GetFor("ok.ru"));
        Assert.Null(registry.GetFor("filemoon"));
    }

    private sealed class FakeResolver : IHosterResolver
    {
        public FakeResolver(string hoster) { Hoster = hoster; }
        public string Hoster { get; }
        public bool IsHttpOnly => true;
        public Task<ResolvedSource> ResolveAsync(Data.Entities.Mirror m, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }
}
