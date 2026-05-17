using System.Text.RegularExpressions;
using AnimeIndex.Api.Data.Entities;

namespace AnimeIndex.Api.Infrastructure.Resolvers;

/// <summary>
/// Mediafire resolver — HTTP-only, single regex against the file landing page.
/// Pattern: GET mediafire.com/file/{id}/ → find the signed download href → direct MP4.
/// Signed URLs are session-scoped but stay valid ~30 minutes — plenty for Phase A→B gap.
/// File quality varies: some mirrors are 720p (~280 MB), others 360p (~45 MB).
/// </summary>
public sealed class MediafireResolver : IHosterResolver
{
    public string Hoster => "mediafire";
    public bool IsHttpOnly => true;

    private readonly IHttpClientFactory _httpFactory;

    private static readonly Regex FileIdRegex = new(
        @"mediafire\.com/(?:file(?:_premium)?)/([a-zA-Z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Mediafire embeds the signed CDN URL in an <a href="https://downloadN.mediafire.com/...">
    private static readonly Regex DownloadHrefRegex = new(
        @"href=""(https://download\d*\.mediafire\.com/[^""]+)""",
        RegexOptions.Compiled);

    public MediafireResolver(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public async Task<ResolvedSource> ResolveAsync(Mirror mirror, CancellationToken ct = default)
    {
        var idMatch = FileIdRegex.Match(mirror.EmbedUrl);
        if (!idMatch.Success)
            throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                $"Could not extract Mediafire file ID from {mirror.EmbedUrl}");

        var id = idMatch.Groups[1].Value;
        var pageUrl = $"https://www.mediafire.com/file/{id}/";
        var client = _httpFactory.CreateClient("resolver");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (!res.IsSuccessStatusCode)
                throw new ResolverException(Hoster, ResolverFailureReason.EmbedUnavailable,
                    $"Mediafire page returned {(int)res.StatusCode}");

            var html = await res.Content.ReadAsStringAsync(ct);

            var dlMatch = DownloadHrefRegex.Match(html);
            if (!dlMatch.Success)
                throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                    "Mediafire download link not found in page");

            var url = dlMatch.Groups[1].Value;

            return new ResolvedSource(
                Url: url,
                Format: SourceFormat.Mp4,
                Headers: null,
                Subtitles: null,
                Qualities: new[] { new QualityVariant(mirror.QualityLabel, url, null) },
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
                ProxyRequired: false,
                Hoster: Hoster);
        }
        catch (ResolverException) { throw; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ResolverException(Hoster, ResolverFailureReason.Timeout, "Mediafire resolve timeout");
        }
        catch (HttpRequestException ex)
        {
            throw new ResolverException(Hoster, ResolverFailureReason.NetworkError, ex.Message, ex);
        }
    }
}
