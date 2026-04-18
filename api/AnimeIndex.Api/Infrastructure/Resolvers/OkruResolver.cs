using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnimeIndex.Api.Data.Entities;

namespace AnimeIndex.Api.Infrastructure.Resolvers;

/// <summary>
/// OK.ru / Odnoklassniki resolver — HTTP-only.
/// Pattern: GET embed page → parse data-options="..." attribute on the player div.
/// data-options is HTML-encoded JSON containing flashvars.metadata which is itself
/// a JSON string with videos[] array sorted by quality (mobile, lowest, low, sd, hd, full).
/// Tokens are time-limited but NOT IP-bound — works fine when extracted server-side.
/// </summary>
public sealed class OkruResolver : IHosterResolver
{
    public string Hoster => "okru";
    public bool IsHttpOnly => true;

    private readonly IHttpClientFactory _httpFactory;

    private static readonly Regex DataOptionsRegex = new(
        @"data-options=""([^""]+)""",
        RegexOptions.Compiled);

    public OkruResolver(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public async Task<ResolvedSource> ResolveAsync(Mirror mirror, CancellationToken ct = default)
    {
        var client = _httpFactory.CreateClient("resolver");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, mirror.EmbedUrl);
            req.Headers.TryAddWithoutValidation("Referer", "https://ok.ru/");
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (!res.IsSuccessStatusCode)
                throw new ResolverException(Hoster, ResolverFailureReason.EmbedUnavailable,
                    $"OK.ru embed returned {(int)res.StatusCode}");

            var html = await res.Content.ReadAsStringAsync(ct);
            var optionsMatch = DataOptionsRegex.Match(html);
            if (!optionsMatch.Success)
                throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                    "Could not find data-options attribute in OK.ru embed");

            var optionsJson = WebUtility.HtmlDecode(optionsMatch.Groups[1].Value);
            using var doc = JsonDocument.Parse(optionsJson);
            if (!doc.RootElement.TryGetProperty("flashvars", out var flashvars)
                || !flashvars.TryGetProperty("metadata", out var metadataEl))
                throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                    "OK.ru data-options missing flashvars.metadata");

            var metadataJson = metadataEl.GetString()
                ?? throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged, "metadata is null");
            using var meta = JsonDocument.Parse(metadataJson);

            if (!meta.RootElement.TryGetProperty("videos", out var videos) || videos.GetArrayLength() == 0)
                throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                    "OK.ru metadata.videos is empty");

            var qualities = new List<QualityVariant>();
            string? bestUrl = null;
            int bestHeight = 0;
            foreach (var v in videos.EnumerateArray())
            {
                var name = v.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = v.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(url)) continue;

                var height = MapQualityName(name);
                qualities.Add(new QualityVariant(height, url, null));
                if (height > bestHeight)
                {
                    bestHeight = height;
                    bestUrl = url;
                }
            }

            if (bestUrl is null)
                throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                    "OK.ru returned no playable video URLs");

            return new ResolvedSource(
                Url: bestUrl,
                Format: SourceFormat.Mp4,
                Headers: new Dictionary<string, string> { ["Referer"] = "https://ok.ru/" },
                Subtitles: null,
                Qualities: qualities,
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(2),
                ProxyRequired: false,
                Hoster: Hoster);
        }
        catch (JsonException ex)
        {
            throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged, ex.Message, ex);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ResolverException(Hoster, ResolverFailureReason.Timeout, "OK.ru resolve timeout");
        }
        catch (HttpRequestException ex)
        {
            throw new ResolverException(Hoster, ResolverFailureReason.NetworkError, ex.Message, ex);
        }
    }

    private static int MapQualityName(string? name) => name switch
    {
        "mobile" => 144,
        "lowest" => 240,
        "low" => 360,
        "sd" => 480,
        "hd" => 720,
        "full" => 1080,
        "quad" => 1440,
        "ultra" => 2160,
        _ => 0
    };
}
