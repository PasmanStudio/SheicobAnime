using System.Text.RegularExpressions;
using AnimeIndex.Api.Data.Entities;

namespace AnimeIndex.Api.Infrastructure.Resolvers;

/// <summary>
/// VOE (voe.sx) resolver — HTTP-only, two-step redirect.
///
/// Step 1: GET https://voe.sx/e/{id}
///   → 751-byte HTML with JS redirect:
///     window.location.href = 'https://maryspecialwatch.com/e/{id}'
///   (The actual CDN domain rotates; we follow whatever URL is in the JS.)
///
/// Step 2: GET {redirectUrl}
///   → Full player HTML containing:
///     var source='https://cdn.example.com/path/filename.mp4';
///   → Direct MP4 — no token, no packed JS, no m3u8.
///
/// Tested 2026-05-08 against https://voe.sx/e/niwtatryolqn — pattern confirmed.
/// </summary>
public sealed class VoeResolver : IHosterResolver
{
    public string Hoster => "voe";
    public bool IsHttpOnly => true;

    private readonly IHttpClientFactory _httpFactory;

    private static readonly Regex EmbedIdRegex = new(
        @"voe\.sx/e/([a-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Step 1 response: extracts the CDN redirect URL from the JS block.
    // The permanentToken branch uses currentUrl.href — ignore it; the else branch
    // is always the CDN URL we want.
    private static readonly Regex RedirectUrlRegex = new(
        @"window\.location\.href\s*=\s*'(https?://[^']+/e/[^']+)'",
        RegexOptions.Compiled);

    // Step 2 response: var source='URL'
    private static readonly Regex VarSourceRegex = new(
        @"var\s+source\s*=\s*'(https?://[^']+)'",
        RegexOptions.Compiled);

    public VoeResolver(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public async Task<ResolvedSource> ResolveAsync(Mirror mirror, CancellationToken ct = default)
    {
        var idMatch = EmbedIdRegex.Match(mirror.EmbedUrl);
        if (!idMatch.Success)
            throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                $"Could not extract VOE ID from {mirror.EmbedUrl}");

        var id = idMatch.Groups[1].Value;
        var client = _httpFactory.CreateClient("resolver");

        try
        {
            // ── Step 1: get the CDN redirect URL ────────────────────────────
            var step1Url = $"https://voe.sx/e/{id}";
            using var req1 = new HttpRequestMessage(HttpMethod.Get, step1Url);
            req1.Headers.TryAddWithoutValidation("Referer", "https://voe.sx/");
            req1.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

            using var res1 = await client.SendAsync(req1, HttpCompletionOption.ResponseContentRead, ct);
            if (!res1.IsSuccessStatusCode)
                throw new ResolverException(Hoster, ResolverFailureReason.EmbedUnavailable,
                    $"VOE embed page returned {(int)res1.StatusCode}");

            var step1Html = await res1.Content.ReadAsStringAsync(ct);
            var redirectMatch = RedirectUrlRegex.Match(step1Html);
            if (!redirectMatch.Success)
                throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                    "Could not find CDN redirect URL in VOE step-1 HTML");

            var cdnUrl = redirectMatch.Groups[1].Value;

            // ── Step 2: get the player page with var source='...' ───────────
            using var req2 = new HttpRequestMessage(HttpMethod.Get, cdnUrl);
            req2.Headers.TryAddWithoutValidation("Referer", "https://voe.sx/");
            req2.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

            using var res2 = await client.SendAsync(req2, HttpCompletionOption.ResponseContentRead, ct);
            if (!res2.IsSuccessStatusCode)
                throw new ResolverException(Hoster, ResolverFailureReason.EmbedUnavailable,
                    $"VOE CDN page returned {(int)res2.StatusCode}");

            var step2Html = await res2.Content.ReadAsStringAsync(ct);
            var sourceMatch = VarSourceRegex.Match(step2Html);
            if (!sourceMatch.Success)
                throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                    "Could not find 'var source' in VOE CDN HTML");

            var mp4Url = sourceMatch.Groups[1].Value;

            return new ResolvedSource(
                Url: mp4Url,
                Format: SourceFormat.Mp4,
                Headers: null,
                Subtitles: null,
                Qualities: new[] { new QualityVariant(mirror.QualityLabel, mp4Url, null) },
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(6),
                ProxyRequired: false,
                Hoster: Hoster);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ResolverException(Hoster, ResolverFailureReason.Timeout, "VOE resolve timeout");
        }
        catch (ResolverException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ResolverException(Hoster, ResolverFailureReason.NetworkError,
                $"VOE resolve failed: {ex.Message}", ex);
        }
    }
}
