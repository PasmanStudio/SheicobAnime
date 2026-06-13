namespace AnimeIndex.Api.Infrastructure;

/// <summary>
/// Reads or provisions an anonymous device_id UUID stored in the `sheicob_did` cookie.
/// Used by watch_progress endpoints as the only identifier of a viewer (no accounts).
/// </summary>
public sealed class DeviceIdMiddleware
{
    public const string CookieName = "sheicob_did";
    public const string HttpContextItemKey = "DeviceId";

    private static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(730); // ~2 years

    private readonly RequestDelegate _next;

    public DeviceIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        Guid deviceId;
        var raw = context.Request.Cookies[CookieName];

        if (string.IsNullOrWhiteSpace(raw) || !Guid.TryParse(raw, out deviceId))
        {
            deviceId = Guid.NewGuid();

            // El frontend (Cloudflare) y el API (Render) son sitios distintos, así
            // que los fetch del progreso son CROSS-SITE. Con SameSite=Lax el browser
            // NO manda la cookie en esos requests → cada llamada genera un device id
            // nuevo y "Continuar viendo" siempre vuelve vacío. SameSite=None+Secure
            // es obligatorio para que el progreso persista. En dev (localhost http)
            // None+Secure no funciona → Lax.
            var isDev = context.RequestServices
                .GetRequiredService<IWebHostEnvironment>().IsDevelopment();

            context.Response.Cookies.Append(CookieName, deviceId.ToString(), new CookieOptions
            {
                HttpOnly = false, // readable by JS intentionally — frontend uses it too
                Secure = !isDev,
                SameSite = isDev ? SameSiteMode.Lax : SameSiteMode.None,
                Expires = DateTimeOffset.UtcNow.Add(CookieLifetime),
                IsEssential = true, // functional, not marketing — GDPR exemption
                Path = "/",
            });
        }

        context.Items[HttpContextItemKey] = deviceId;
        await _next(context);
    }
}

public static class DeviceIdExtensions
{
    public static Guid GetDeviceId(this HttpContext context)
    {
        if (context.Items.TryGetValue(DeviceIdMiddleware.HttpContextItemKey, out var v)
            && v is Guid id)
        {
            return id;
        }
        throw new InvalidOperationException(
            "DeviceId not set. Ensure DeviceIdMiddleware is registered before the endpoint.");
    }
}
