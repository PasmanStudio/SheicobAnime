namespace AnimeIndex.Api.Infrastructure.Resolvers;

/// <summary>
/// Result of resolving a third-party hoster embed into a playable HLS/MP4 source.
/// Returned by IHosterResolver implementations.
/// </summary>
public record ResolvedSource(
    string Url,
    SourceFormat Format,
    Dictionary<string, string>? Headers,
    IReadOnlyList<SubtitleTrack>? Subtitles,
    IReadOnlyList<QualityVariant>? Qualities,
    DateTimeOffset ExpiresAt,
    bool ProxyRequired,
    string Hoster);

public enum SourceFormat
{
    Hls,    // .m3u8
    Mp4,    // direct mp4
    Dash    // .mpd
}

public record SubtitleTrack(string Language, string Label, string Url);

public record QualityVariant(int Height, string Url, int? BandwidthKbps);
