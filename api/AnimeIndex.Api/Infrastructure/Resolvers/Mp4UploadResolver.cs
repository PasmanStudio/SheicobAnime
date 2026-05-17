using System.Text.RegularExpressions;
using AnimeIndex.Api.Data.Entities;

namespace AnimeIndex.Api.Infrastructure.Resolvers;

/// <summary>
/// Mp4Upload resolver — HTTP-only (no Playwright). Embed page contains packed JS
/// with a direct .mp4 source URL. Token has loose IP binding (works from user IP if
/// extracted server-side because mp4upload uses Referer-based validation, not strict IP).
/// </summary>
public sealed class Mp4UploadResolver : IHosterResolver
{
    public string Hoster => "mp4upload";
    public bool IsHttpOnly => true;

    private readonly IHttpClientFactory _httpFactory;

    // Mp4Upload's player calls `player.src({ type: "video/mp4", src: "https://aN.mp4upload.com:PORT/d/.../video.mp4" })`.
    // The old loose pattern falsely matched `/player/videojs/video.min.js`. Anchor to player.src({ ... src: "..." }).
    private static readonly Regex Mp4SourceRegex = new(
        @"player\.src\s*\(\s*\{[^}]*?src\s*:\s*[""'](https?://[^""'\s]+\.mp4[^""'\s]*)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex EmbedIdRegex = new(
        @"mp4upload\.com/(?:embed-)?([a-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Mp4UploadResolver(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public async Task<ResolvedSource> ResolveAsync(Mirror mirror, CancellationToken ct = default)
    {
        var idMatch = EmbedIdRegex.Match(mirror.EmbedUrl);
        if (!idMatch.Success)
            throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                $"Could not extract Mp4Upload ID from {mirror.EmbedUrl}");

        var embedUrl = $"https://www.mp4upload.com/embed-{idMatch.Groups[1].Value}.html";
        var client = _httpFactory.CreateClient("resolver");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, embedUrl);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.mp4upload.com/");
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (!res.IsSuccessStatusCode)
                throw new ResolverException(Hoster, ResolverFailureReason.EmbedUnavailable,
                    $"Mp4Upload embed returned {(int)res.StatusCode}");

            var html = await res.Content.ReadAsStringAsync(ct);

            // Mp4Upload returns HTTP 200 for deleted/expired files but shows an error page.
            if (html.Contains("File was deleted", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("file has been deleted", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("no longer available", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("File Not Found", StringComparison.OrdinalIgnoreCase))
                throw new ResolverException(Hoster, ResolverFailureReason.EmbedUnavailable,
                    "Mp4Upload file deleted or expired");

            var unpacked = PackedJsUnpacker.Unpack(html);

            var srcMatch = Mp4SourceRegex.Match(unpacked);
            if (!srcMatch.Success)
                throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                    $"Could not find .mp4 source in unpacked JS (html_len={html.Length} unpacked_len={unpacked.Length})");

            var url = srcMatch.Groups[1].Value;
            return new ResolvedSource(
                Url: url,
                Format: SourceFormat.Mp4,
                Headers: new Dictionary<string, string>
                {
                    ["Referer"] = "https://www.mp4upload.com/"
                },
                Subtitles: null,
                Qualities: new[] { new QualityVariant(mirror.QualityLabel, url, null) },
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(2),
                ProxyRequired: false,
                Hoster: Hoster);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ResolverException(Hoster, ResolverFailureReason.Timeout, "Mp4Upload resolve timeout");
        }
        catch (HttpRequestException ex)
        {
            throw new ResolverException(Hoster, ResolverFailureReason.NetworkError, ex.Message, ex);
        }
    }
}
