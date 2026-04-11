using Hangfire.Dashboard;

namespace AnimeIndex.Api.Infrastructure.Auth;

public class HangfireDashboardAuthFilter(IConfiguration configuration, IWebHostEnvironment environment) : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();

        if (environment.IsDevelopment())
            return true;

        var expected = configuration["HANGFIRE_DASHBOARD_PASSWORD"];
        if (string.IsNullOrEmpty(expected))
            return false;

        var provided = httpContext.Request.Headers["X-Hangfire-Password"].FirstOrDefault()
                       ?? httpContext.Request.Query["password"].FirstOrDefault();

        return string.Equals(provided, expected, StringComparison.Ordinal);
    }
}
