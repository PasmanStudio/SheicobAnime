namespace AnimeIndex.Api.DTOs;

/// <summary>Proyección mínima para los sitemaps de episodios del sitio.</summary>
public record EpisodeSitemapDto(string SeriesSlug, short EpisodeNumber, DateTime CreatedAt);
