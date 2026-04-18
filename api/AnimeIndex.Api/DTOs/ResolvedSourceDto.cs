namespace AnimeIndex.Api.DTOs;

/// <summary>
/// Response from POST /mirrors/{id}/resolve — fresh playable URL extracted from the hoster.
/// Frontend uses this to feed HLS.js or a direct &lt;video&gt; element instead of an iframe.
/// </summary>
public record ResolvedSourceDto(
    string Url,
    string Format,             // "hls" | "mp4" | "dash"
    Dictionary<string, string>? Headers,
    IReadOnlyList<SubtitleDto>? Subtitles,
    IReadOnlyList<QualityDto>? Qualities,
    DateTimeOffset ExpiresAt,
    bool ProxyRequired,
    string Hoster);

public record SubtitleDto(string Language, string Label, string Url);

public record QualityDto(int Height, string Url, int? BandwidthKbps);

/// <summary>
/// Item in the resolvable-set response. The frontend uses these to render
/// resolvable mirrors as "Sheicob" branded buttons.
/// </summary>
public record ResolvableMirrorDto(
    Guid MirrorId,
    string ProviderName,
    short QualityLabel,
    short Priority,
    bool Resolvable);
