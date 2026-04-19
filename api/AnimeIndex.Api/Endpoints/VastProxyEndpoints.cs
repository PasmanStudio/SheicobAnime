using System.Xml.Linq;

namespace AnimeIndex.Api.Endpoints;

/// <summary>
/// Server-side VAST proxy — fetches VAST XML from an ad network (ExoClick) and
/// returns it to the frontend.  This is needed because ExoClick's VAST endpoint
/// (s.magsrv.com) does NOT send Access-Control-Allow-Origin headers, so browser
/// fetch() from our Vercel origin is blocked by CORS.
///
/// When "VAST Wrapper Support" is enabled in ExoClick, the initial response may
/// be a &lt;Wrapper&gt; pointing to another VAST endpoint.  This proxy follows
/// the chain (up to 5 hops) server-side so the frontend always receives a
/// fully-resolved InLine VAST with the final MediaFile URL.
///
/// Only whitelisted VAST hosts are proxied to prevent open-relay abuse.
/// </summary>
public static class VastProxyEndpoints
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "s.magsrv.com",        // ExoClick
        "syndication.exoclick.com",
        "ads.exoclick.com",
    };

    private const int MaxWrapperDepth = 5;

    public static void MapVastProxyEndpoints(this WebApplication app)
    {
        app.MapGet("/vast", ProxyVast)
           .WithTags("Ads")
           .RequireRateLimiting("fixed"); // use the global 60 req/min/IP limiter
    }

    private static async Task<IResult> ProxyVast(
        HttpContext ctx,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory,
        string url,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("VastProxy");

        // Validate the initial URL points to a whitelisted ad network
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
            || !AllowedHosts.Contains(parsed.Host))
        {
            return Results.StatusCode(403);
        }

        var client = httpFactory.CreateClient("vast");
        var userAgent = ctx.Request.Headers.UserAgent.ToString();
        var clientIp = ctx.Connection.RemoteIpAddress?.ToString();
        var referer = $"{ctx.Request.Scheme}://{ctx.Request.Host}/";

        try
        {
            var currentUrl = url;
            string xml = string.Empty;

            for (var depth = 0; depth < MaxWrapperDepth; depth++)
            {
                xml = await FetchVastXml(client, currentUrl, userAgent, clientIp, referer, ct);

                // Try to parse and check for Wrapper chain
                var nextUrl = ExtractWrapperUrl(xml);
                if (nextUrl is null)
                    break; // InLine or empty VAST — we're done

                logger.LogDebug("VAST Wrapper hop {Depth}: {WrapperUrl}", depth + 1, nextUrl);
                currentUrl = nextUrl;
            }

            // Return the final (InLine or empty) VAST XML
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            return Results.Content(xml, "text/xml; charset=utf-8");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "VAST proxy fetch failed for {Host}", parsed.Host);
            return Results.StatusCode(502);
        }
    }

    private static async Task<string> FetchVastXml(
        HttpClient client, string url, string userAgent, string? clientIp, string referer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(userAgent))
            req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        if (!string.IsNullOrEmpty(clientIp))
            req.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
        req.Headers.TryAddWithoutValidation("Referer", referer);

        using var res = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// If the VAST XML contains a Wrapper element with a VASTAdTagURI, return it.
    /// Otherwise return null (meaning we have a final InLine or empty VAST).
    /// </summary>
    private static string? ExtractWrapperUrl(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            // VAST 3.0/4.0: <VAST><Ad><Wrapper><VASTAdTagURI>...</VASTAdTagURI></Wrapper></Ad></VAST>
            var wrapper = doc.Descendants("Wrapper").FirstOrDefault();
            var tagUri = wrapper?.Element("VASTAdTagURI")?.Value?.Trim();
            if (!string.IsNullOrEmpty(tagUri) && Uri.TryCreate(tagUri, UriKind.Absolute, out _))
                return tagUri;
        }
        catch
        {
            // Malformed XML — treat as final
        }
        return null;
    }
}
