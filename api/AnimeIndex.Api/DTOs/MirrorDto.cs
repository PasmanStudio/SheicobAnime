namespace AnimeIndex.Api.DTOs;

public record MirrorDto(
    Guid Id,
    Guid EpisodeId,
    string ProviderName,
    string EmbedUrl,
    short QualityLabel,
    short Priority,
    bool IsActive);
