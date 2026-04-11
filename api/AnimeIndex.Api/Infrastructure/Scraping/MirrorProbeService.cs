namespace AnimeIndex.Api.Infrastructure.Scraping;

/// <summary>
/// Probes a URL to determine if it is publicly embeddable in an iframe.
/// Checks: HTTP 200 + no X-Frame-Options DENY/SAMEORIGIN + no CSP frame-ancestors 'none'/'self'.
/// Called on EVERY mirror URL before it is stored. Never bypass this check.
/// </summary>
public class MirrorProbeService(IHttpClientFactory httpClientFactory)
{
    private readonly HttpClient _http = httpClientFactory.CreateClient("probe");

    public async Task<bool> IsEmbeddableAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode) return false;

            // ── X-Frame-Options ─────────────────────────────────
            if (response.Headers.TryGetValues("X-Frame-Options", out var xfoValues))
            {
                var xfo = string.Join(",", xfoValues).ToUpperInvariant();
                if (xfo.Contains("DENY") || xfo.Contains("SAMEORIGIN"))
                    return false;
            }

            // ── Content-Security-Policy frame-ancestors ──────────
            if (response.Headers.TryGetValues("Content-Security-Policy", out var cspValues))
            {
                var csp = string.Join(" ", cspValues).ToLowerInvariant();
                var fa = csp.IndexOf("frame-ancestors", StringComparison.Ordinal);
                if (fa >= 0)
                {
                    var directive = csp[fa..];
                    if (directive.Contains("'none'") || directive.Contains("'self'"))
                        return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
