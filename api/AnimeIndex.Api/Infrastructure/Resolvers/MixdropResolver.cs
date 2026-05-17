using System.Text.RegularExpressions;
using AnimeIndex.Api.Data.Entities;

namespace AnimeIndex.Api.Infrastructure.Resolvers;

/// <summary>
/// Mixdrop resolver — HTTP-only, packed JS decoding.
/// Pattern: GET embed → packed eval() block → decode → MDCore.wurl → direct MP4 on mxcontent.net CDN.
/// Tokens are time-limited but NOT IP-bound — works from cloud IPs (GitHub Actions).
///
/// The Dean-Edwards packer here uses base-10 so decoding is trivial (integer lookup in dict).
/// PackedJsUnpacker cannot handle this block because its regex fails on nested braces in the
/// function body — this resolver uses a targeted regex that anchors on }(' instead.
/// </summary>
public sealed class MixdropResolver : IHosterResolver
{
    public string Hoster => "mixdrop";
    public bool IsHttpOnly => true;

    private readonly IHttpClientFactory _httpFactory;

    private static readonly Regex EmbedIdRegex = new(
        @"(?:mixdrop|mxdrop)[^/]+/(?:e/|d/|f/)?([a-zA-Z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Anchors on }(' which closes the packed function definition and opens the call arguments.
    // [^']+ is safe for the payload because Mixdrop's obfuscated code uses only " for inner strings.
    private static readonly Regex PackedRegex = new(
        @"\}\('([^']+)',\s*(\d+),\s*\d+,\s*'([^']+)'\.split\('\|'\)",
        RegexOptions.Compiled);

    // MDCore.wurl holds the protocol-relative CDN URL ("//host/v2/id.mp4?s=TOKEN...").
    private static readonly Regex WurlRegex = new(
        @"MDCore\.wurl\s*=\s*""(//[^""]+)""",
        RegexOptions.Compiled);

    public MixdropResolver(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public async Task<ResolvedSource> ResolveAsync(Mirror mirror, CancellationToken ct = default)
    {
        var idMatch = EmbedIdRegex.Match(mirror.EmbedUrl);
        if (!idMatch.Success)
            throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                $"Could not extract Mixdrop ID from {mirror.EmbedUrl}");

        var id = idMatch.Groups[1].Value;
        var host = new Uri(mirror.EmbedUrl).Host;
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
                    $"Mixdrop embed returned {(int)res.StatusCode}");

            var html = await res.Content.ReadAsStringAsync(ct);

            var packed = PackedRegex.Match(html);
            if (!packed.Success)
                throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                    "Could not find packed JS block in Mixdrop embed");

            var payload = packed.Groups[1].Value;
            var radix   = int.Parse(packed.Groups[2].Value);
            var dict    = packed.Groups[3].Value.Split('|');
            var decoded = Decode(payload, radix, dict);

            var wurlMatch = WurlRegex.Match(decoded);
            if (!wurlMatch.Success)
                throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                    "MDCore.wurl not found in decoded Mixdrop JS");

            var url = "https:" + wurlMatch.Groups[1].Value;

            return new ResolvedSource(
                Url: url,
                Format: SourceFormat.Mp4,
                Headers: new Dictionary<string, string>
                {
                    ["Referer"] = $"https://{host}/"
                },
                Subtitles: null,
                Qualities: new[] { new QualityVariant(mirror.QualityLabel, url, null) },
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(6),
                ProxyRequired: false,
                Hoster: Hoster);
        }
        catch (ResolverException) { throw; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ResolverException(Hoster, ResolverFailureReason.Timeout, "Mixdrop resolve timeout");
        }
        catch (HttpRequestException ex)
        {
            throw new ResolverException(Hoster, ResolverFailureReason.NetworkError, ex.Message, ex);
        }
    }

    // Dean-Edwards base-N decoder. Mirrors PackedJsUnpacker.ParseRadix logic.
    private static string Decode(string payload, int radix, string[] dict)
    {
        const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        return Regex.Replace(payload, @"\b\w+\b", m =>
        {
            int idx = 0;
            foreach (var c in m.Value)
            {
                var d = Alphabet.IndexOf(c, StringComparison.Ordinal);
                if (d < 0 || d >= radix) return m.Value;
                idx = idx * radix + d;
            }
            return idx >= 0 && idx < dict.Length && dict[idx].Length > 0 ? dict[idx] : m.Value;
        });
    }
}
