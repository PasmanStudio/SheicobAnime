namespace AnimeIndex.Api.DTOs;

public record PaginatedResponse<T>(T[] Data, int Total, int Page, int PageSize);
