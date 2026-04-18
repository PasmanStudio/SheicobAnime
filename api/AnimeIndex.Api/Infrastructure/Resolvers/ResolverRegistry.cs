using AnimeIndex.Api.Data.Entities;

namespace AnimeIndex.Api.Infrastructure.Resolvers;

/// <summary>
/// Routes a Mirror to the appropriate IHosterResolver based on ProviderName.
/// Used by both /mirrors/{id}/resolve and /mirrors/{episodeId}/resolvable-set endpoints.
/// </summary>
public sealed class ResolverRegistry
{
    private readonly Dictionary<string, IHosterResolver> _resolvers;

    public ResolverRegistry(IEnumerable<IHosterResolver> resolvers)
    {
        _resolvers = resolvers.ToDictionary(r => r.Hoster.ToLowerInvariant(), r => r);
    }

    /// <summary>Returns true if a resolver exists for the given hoster name.</summary>
    public bool Supports(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return false;
        return _resolvers.ContainsKey(NormalizeHosterName(providerName));
    }

    /// <summary>Returns the resolver for a hoster, or null if not supported.</summary>
    public IHosterResolver? GetFor(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return null;
        return _resolvers.GetValueOrDefault(NormalizeHosterName(providerName));
    }

    public IHosterResolver? GetFor(Mirror mirror) => GetFor(mirror.ProviderName);

    /// <summary>All supported hoster names (lowercase).</summary>
    public IReadOnlyCollection<string> SupportedHosters => _resolvers.Keys;

    /// <summary>
    /// Normalizes provider names so e.g. "Mp4Upload", "MP4UPLOAD", "mp4upload" all map to "mp4upload".
    /// Strips common suffixes/prefixes seen in scraped data ("-hls", "_v2", spaces).
    /// </summary>
    internal static string NormalizeHosterName(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        // Common aliases observed in scraped data
        return n switch
        {
            "mp4" or "mp4upload" or "mp4-upload" => "mp4upload",
            "sw" or "streamwish" or "stream-wish" or "streamwsh" => "streamwish",
            "vidhide" or "vid-hide" or "vidhidepro" => "vidhide",
            "ok" or "okru" or "ok.ru" or "odnoklassniki" => "okru",
            "voe" or "voe.sx" => "voe",
            "mixdrop" or "mxdrop" or "mix-drop" => "mixdrop",
            "filemoon" or "file-moon" or "fmoon" => "filemoon",
            _ => n
        };
    }
}
