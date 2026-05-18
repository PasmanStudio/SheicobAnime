using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnimeIndex.Api.Data.Entities;

namespace AnimeIndex.Api.Infrastructure.Resolvers;

/// <summary>
/// Vidhide / VidHidePro resolver — HTTP-only.
/// Pattern: GET embed page → extract packed JS → unpack → find .m3u8 sources array.
/// Newer Vidhide versions also expose POST /api/source/{id} returning JSON.
/// We try the API first (faster), fall back to packed-JS scraping.
/// </summary>
public sealed class VidhideResolver : IHosterResolver
{
    public string Hoster => "vidhide";
    public bool IsHttpOnly => true;

    private readonly IHttpClientFactory _httpFactory;

    private static readonly Regex EmbedIdRegex = new(
        @"(?:vidhide|vid-hide|vidhidepro|d000d|kinoger|streamhide)[^/]*\.(?:com|net|to|pro|sx)/(?:embed/|embed-|e/|d/|v/)?([a-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Vidhide uses the same indirection as Streamwish (sources:[{file:links.hlsN||...}] with URLs
    // defined in separate `links.hlsN="..."` assignments). Scan for the .m3u8 URL directly.
    private static readonly Regex M3u8Regex = new(
        @"(https?://[^\s""'\\<>]+\.m3u8[^\s""'\\<>]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public VidhideResolver(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public async Task<ResolvedSource> ResolveAsync(Mirror mirror, CancellationToken ct = default)
    {
        var idMatch = EmbedIdRegex.Match(mirror.EmbedUrl);
        if (!idMatch.Success)
            throw new ResolverException(Hoster, ResolverFailureReason.EmbedUnavailable,
                $"Unsupported Vidhide host: {mirror.EmbedUrl}");

        var id = idMatch.Groups[1].Value;
        var host = new Uri(mirror.EmbedUrl).Host;
        // Vidhide's current URL scheme is /embed/{id} — the old /embed-{id}.html returns 404.
        var embedUrl = $"https://{host}/embed/{id}";
        var client = _httpFactory.CreateClient("resolver");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, embedUrl);
            req.Headers.TryAddWithoutValidation("Referer", $"https://{host}/");
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (!res.IsSuccessStatusCode)
                throw new ResolverException(Hoster, ResolverFailureReason.EmbedUnavailable,
                    $"Vidhide embed returned {(int)res.StatusCode}");

            var html = await res.Content.ReadAsStringAsync(ct);
            var unpacked = PackedJsUnpacker.Unpack(html);

            var m3u8Match = M3u8Regex.Match(unpacked);
            if (!m3u8Match.Success)
                throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                    "Could not find .m3u8 source in Vidhide unpacked JS");

            var url = m3u8Match.Groups[1].Value;
            return new ResolvedSource(
                Url: url,
                Format: SourceFormat.Hls,
                Headers: new Dictionary<string, string>
                {
                    ["Referer"] = $"https://{host}/"
                },
                Subtitles: null,
                Qualities: null, // master.m3u8 will expose levels via HLS.js
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(2),
                ProxyRequired: false,
                Hoster: Hoster);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ResolverException(Hoster, ResolverFailureReason.Timeout, "Vidhide resolve timeout");
        }
        catch (HttpRequestException ex)
        {
            throw new ResolverException(Hoster, ResolverFailureReason.NetworkError, ex.Message, ex);
        }
    }
}
