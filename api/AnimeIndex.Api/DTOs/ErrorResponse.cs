namespace AnimeIndex.Api.DTOs;

public record ErrorResponse(string Error, string Code, object? Details = null);
