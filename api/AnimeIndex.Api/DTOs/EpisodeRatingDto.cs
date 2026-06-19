namespace AnimeIndex.Api.DTOs;

/// <summary>Native rating aggregate for an episode + the requesting device's own rating.</summary>
public record EpisodeRatingStatsDto(double Average, int Count, short? MyRating);

public record RateEpisodeRequest(int Rating);
