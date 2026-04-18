using System.Text.RegularExpressions;
using AnimeIndex.Api.Data.Entities;

namespace AnimeIndex.Api.Infrastructure.Resolvers;

/// <summary>
/// Streamwish resolver — HTTP-only via packed-JS unpacking.
/// Pattern: GET embed → packed eval() block contains `sources:[{file:"...m3u8?token=..."}]`.
/// Token is IP-bound for segment requests but the manifest itself often works for any IP
/// for ~30 minutes. Set ProxyRequired=true so the frontend hits us first; we set Referer header.
/// </summary>
public sealed class StreamwishResolver : IHosterResolver
{
    public string Hoster => "streamwish";
    public bool IsHttpOnly => true;

    private readonly IHttpClientFactory _httpFactory;

    private static readonly Regex EmbedIdRegex = new(
        @"(?:streamwish|swhoi|cilootv|swdyu|sfastwish|streamwsh)\.[^/]+/(?:e/|f/|embed-)?([a-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SourceRegex = new(
        @"file\s*:\s*[""'](https?://[^""'\s]+\.m3u8[^""'\s]*)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public StreamwishResolver(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public async Task<ResolvedSource> ResolveAsync(Mirror mirror, CancellationToken ct = default)
    {
        var idMatch = EmbedIdRegex.Match(mirror.EmbedUrl);
        if (!idMatch.Success)
            throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                $"Could not extract Streamwish ID from {mirror.EmbedUrl}");

        var host = new Uri(mirror.EmbedUrl).Host;
        var id = idMatch.Groups[1].Value;
        var embedUrl = $"https://{host}/e/{id}";

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
                    $"Streamwish embed returned {(int)res.StatusCode}");

            var html = await res.Content.ReadAsStringAsync(ct);
            var unpacked = PackedJsUnpacker.Unpack(html);

            var match = SourceRegex.Match(unpacked);
            if (!match.Success)
                throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                    "Streamwish source not found after unpack");

            var url = match.Groups[1].Value;
            return new ResolvedSource(
                Url: url,
                Format: SourceFormat.Hls,
                Headers: new Dictionary<string, string>
                {
                    ["Referer"] = $"https://{host}/"
                },
                Subtitles: null,
                Qualities: null,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
                ProxyRequired: true, // tokens often IP-bound — proxy the manifest
                Hoster: Hoster);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ResolverException(Hoster, ResolverFailureReason.Timeout, "Streamwish resolve timeout");
        }
        catch (HttpRequestException ex)
        {
            throw new ResolverException(Hoster, ResolverFailureReason.NetworkError, ex.Message, ex);
        }
    }
}
