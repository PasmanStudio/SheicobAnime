using AnimeIndex.Api.DTOs;

namespace AnimeIndex.Api.Infrastructure.Auth;

public class AdminKeyEndpointFilter(IConfiguration configuration) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var expectedKey = configuration["ADMIN_API_KEY"];

        if (string.IsNullOrEmpty(expectedKey))
            return Results.Json(
                new ErrorResponse("Admin API key not configured", "ADMIN_NOT_CONFIGURED"),
                statusCode: 503);

        var providedKey = context.HttpContext.Request.Headers["X-Admin-Key"].FirstOrDefault();

        if (string.IsNullOrEmpty(providedKey) || !string.Equals(providedKey, expectedKey, StringComparison.Ordinal))
            return Results.Json(
                new ErrorResponse("Invalid or missing admin key", "UNAUTHORIZED"),
                statusCode: 401);

        return await next(context);
    }
}
