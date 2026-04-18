using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AnimeIndex.Api.Infrastructure.Proxy;

namespace AnimeIndex.Api.Endpoints;

/// <summary>
/// Streaming proxy for hoster URLs that require a Referer header (which browsers
/// can't set for cross-origin &lt;video&gt; / fetch). Tokens are short-lived HMAC-
/// signed so Sheicob can't be used as an open relay.
///
/// - MP4 / arbitrary binary: streams bytes with HTTP Range pass-through.
/// - HLS (.m3u8): fetches the manifest and rewrites every nested URI to route
///   segments / variant playlists through this same endpoint (so Referer is
///   preserved for every downstream request).
/// </summary>
public static class ProxyEndpoints
{
    // Upstream headers we want to forward to the client
    private static readonly string[] ForwardResponseHeaders =
    {
        "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges",
        "Last-Modified", "ETag", "Cache-Control"
    };

    public static void MapProxyEndpoints(this WebApplication app)
    {
        app.MapGet("/proxy/stream", StreamProxy)
           .WithTags("Proxy")
           .DisableRateLimiting(); // hot path — video player issues many Range requests
    }

    private static async Task<IResult> StreamProxy(
        HttpContext ctx,
        IHttpClientFactory httpFactory,
        ProxyUrlSigner signer,
        ILoggerFactory loggerFactory,
        string u,
        string r,
        long exp,
        string sig,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("ProxyEndpoints");

        if (!signer.TryVerify(u, r, exp, sig, out var upstreamUrl, out var referer))
        {
            return Results.StatusCode((int)HttpStatusCode.Forbidden);
        }

        var client = httpFactory.CreateClient("resolver");
        using var req = new HttpRequestMessage(HttpMethod.Get, upstreamUrl);

        // Referer is the whole point of this proxy
        if (!string.IsNullOrEmpty(referer))
            req.Headers.Referrer = new Uri(referer);

        // Forward Range / If-* headers so <video> scrubbing works
        if (ctx.Request.Headers.TryGetValue("Range", out var range))
            req.Headers.TryAddWithoutValidation("Range", range.ToArray());
        if (ctx.Request.Headers.TryGetValue("If-Range", out var ifRange))
            req.Headers.TryAddWithoutValidation("If-Range", ifRange.ToArray());

        // Pretend to be a browser so hosters don't 403 us
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        req.Headers.Accept.ParseAdd("*/*");

        HttpResponseMessage upstream;
        try
        {
            upstream = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Proxy upstream fetch failed for {Url}", upstreamUrl);
            return Results.StatusCode((int)HttpStatusCode.BadGateway);
        }

        // CORS — allow any origin since URLs are signed
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Expose-Headers"] = "Content-Length, Content-Range, Accept-Ranges";

        ctx.Response.StatusCode = (int)upstream.StatusCode;

        // Copy select headers
        foreach (var name in ForwardResponseHeaders)
        {
            if (upstream.Content.Headers.TryGetValues(name, out var vals))
                ctx.Response.Headers[name] = vals.ToArray();
            else if (upstream.Headers.TryGetValues(name, out var hvals))
                ctx.Response.Headers[name] = hvals.ToArray();
        }

        // Detect HLS manifests — rewrite URIs inside so segments route through us too
        var contentType = upstream.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var looksLikeManifest = contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
            || upstreamUrl.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || upstreamUrl.Contains(".m3u8?", StringComparison.OrdinalIgnoreCase);

        if (looksLikeManifest && upstream.IsSuccessStatusCode)
        {
            try
            {
                var manifestText = await upstream.Content.ReadAsStringAsync(ct);
                var rewritten = RewriteM3u8(manifestText, upstreamUrl, referer, signer);
                var bytes = Encoding.UTF8.GetBytes(rewritten);
                ctx.Response.ContentType = "application/vnd.apple.mpegurl";
                ctx.Response.ContentLength = bytes.Length;
                // Content-Length already set — clear Content-Range we may have copied for partial
                ctx.Response.Headers.Remove("Content-Range");
                ctx.Response.Headers.Remove("Accept-Ranges");
                await ctx.Response.Body.WriteAsync(bytes, ct);
                upstream.Dispose();
                return Results.Empty;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Manifest rewrite failed, falling through to raw stream");
            }
        }

        // Binary pass-through
        try
        {
            await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
            await upstreamStream.CopyToAsync(ctx.Response.Body, 81920, ct);
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        finally
        {
            upstream.Dispose();
        }
        return Results.Empty;
    }

    /// <summary>
    /// Rewrites every non-comment URI in an HLS manifest so it routes through
    /// /proxy/stream. Supports relative (segment.ts), absolute-path (/foo.ts),
    /// and absolute-URL segments, plus KEY/MAP URI="..." attributes.
    /// </summary>
    private static string RewriteM3u8(string text, string manifestUrl, string referer, ProxyUrlSigner signer)
    {
        var baseUri = new Uri(manifestUrl);
        var sb = new StringBuilder(text.Length + 2048);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.Length == 0)
            {
                sb.Append('\n');
                continue;
            }

            if (line.StartsWith('#'))
            {
                // Rewrite URI="..." occurrences inside tag lines (EXT-X-KEY, EXT-X-MAP, etc.)
                sb.Append(RewriteUriAttrs(line, baseUri, referer, signer));
                sb.Append('\n');
                continue;
            }

            // Plain URI line — a segment, variant playlist or init segment
            var abs = ResolveToAbsolute(line, baseUri);
            if (abs is null)
            {
                sb.Append(line);
            }
            else
            {
                sb.Append(signer.BuildProxyPath(abs, referer));
            }
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string RewriteUriAttrs(string line, Uri baseUri, string referer, ProxyUrlSigner signer)
    {
        const string marker = "URI=\"";
        var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return line;

        var sb = new StringBuilder(line.Length + 256);
        var cursor = 0;

        while (idx >= 0)
        {
            sb.Append(line, cursor, idx - cursor);
            sb.Append("URI=\"");

            var start = idx + marker.Length;
            var end = line.IndexOf('"', start);
            if (end < 0)
            {
                sb.Append(line, start, line.Length - start);
                return sb.ToString();
            }

            var uri = line.Substring(start, end - start);
            var abs = ResolveToAbsolute(uri, baseUri);
            sb.Append(abs is null ? uri : signer.BuildProxyPath(abs, referer));
            sb.Append('"');

            cursor = end + 1;
            idx = line.IndexOf(marker, cursor, StringComparison.OrdinalIgnoreCase);
        }

        sb.Append(line, cursor, line.Length - cursor);
        return sb.ToString();
    }

    private static string? ResolveToAbsolute(string uri, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var abs)) return abs.ToString();
        if (Uri.TryCreate(baseUri, uri, out var resolved)) return resolved.ToString();
        return null;
    }
}
