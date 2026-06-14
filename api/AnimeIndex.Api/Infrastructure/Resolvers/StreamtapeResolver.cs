using System.Text.RegularExpressions;
using AnimeIndex.Api.Data.Entities;

namespace AnimeIndex.Api.Infrastructure.Resolvers;

/// <summary>
/// Streamtape resolver — HTTP-only, direct MP4.
///
/// El embed trae un &lt;div id="robotlink"&gt; con un token SEÑUELO y un script que
/// reconstruye el token REAL con .substring():
///   document.getElementById('robotlink').innerHTML =
///     '//streamtape.com/get_video?id'+ ('xcd=...&amp;token=REAL').substring(2).substring(1);
/// El resultado es https://streamtape.com/get_video?id=...&amp;token=... que 302-redirige
/// al MP4 en tapecontent.net (Content-Type: video/mp4, soporta Range).
///
/// El token es IP-bound y expira (param expires=) → para el USUARIO se proxea
/// (ProxyRequired=true). Para el UPLOAD, el scraper resuelve y descarga en la misma
/// corrida/IP de GitHub Actions, así que el token es válido.
///
/// Verificado 2026-06-14 contra streamtape.com/e/QPZ2Vo3gl2C0ZZV (MP4 314 MB, 206).
/// </summary>
public sealed class StreamtapeResolver : IHosterResolver
{
    public string Hoster => "streamtape";
    public bool IsHttpOnly => true;

    private readonly IHttpClientFactory _httpFactory;

    // El id vive en /e/{id} o /v/{id}. Este resolver solo se invoca para mirrors
    // ya etiquetados "streamtape", así que alcanza con leer el id de la ruta.
    private static readonly Regex EmbedIdRegex = new(
        @"/(?:e|v)/([0-9A-Za-z]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // innerHTML = '<prefix>'+ ('<inner>').substring(a).substring(b)...
    private static readonly Regex TokenRegex = new(
        @"innerHTML\s*=\s*'([^']*)'\s*\+\s*\(\s*'([^']*)'\s*\)((?:\.substring\(\d+\))+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SubstringRegex = new(
        @"\.substring\((\d+)\)", RegexOptions.Compiled);

    private static readonly Regex ExpiresRegex = new(
        @"[?&]expires=(\d+)", RegexOptions.Compiled);

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    public StreamtapeResolver(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public async Task<ResolvedSource> ResolveAsync(Mirror mirror, CancellationToken ct = default)
    {
        var idMatch = EmbedIdRegex.Match(new Uri(mirror.EmbedUrl).AbsolutePath);
        if (!idMatch.Success)
            throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                $"Could not extract Streamtape ID from {mirror.EmbedUrl}");

        var host = new Uri(mirror.EmbedUrl).Host;
        var embedUrl = $"https://{host}/e/{idMatch.Groups[1].Value}";

        var client = _httpFactory.CreateClient("resolver");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, embedUrl);
            req.Headers.TryAddWithoutValidation("Referer", $"https://{host}/");
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);

            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (!res.IsSuccessStatusCode)
                throw new ResolverException(Hoster, ResolverFailureReason.EmbedUnavailable,
                    $"Streamtape embed returned {(int)res.StatusCode}");

            var html = await res.Content.ReadAsStringAsync(ct);

            // Puede haber 2 bloques (botlink + robotlink) — ambos reconstruyen el
            // mismo token real. Tomamos el primero que arme una URL get_video válida.
            string? videoUrl = null;
            foreach (Match m in TokenRegex.Matches(html))
            {
                var inner = m.Groups[2].Value;
                foreach (Match s in SubstringRegex.Matches(m.Groups[3].Value))
                {
                    var n = int.Parse(s.Groups[1].Value);
                    inner = n <= inner.Length ? inner[n..] : "";
                }
                var url = m.Groups[1].Value + inner;
                if (url.StartsWith("//")) url = "https:" + url;
                if (url.Contains("get_video", StringComparison.OrdinalIgnoreCase) &&
                    url.Contains("token=", StringComparison.OrdinalIgnoreCase))
                {
                    videoUrl = url;
                    break;
                }
            }

            if (videoUrl is null)
                throw new ResolverException(Hoster, ResolverFailureReason.PatternChanged,
                    "Streamtape get_video URL not found (pattern changed)");

            // El token expira; usamos el param expires= (o +1 h si no está).
            var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
            var exp = ExpiresRegex.Match(videoUrl);
            if (exp.Success && long.TryParse(exp.Groups[1].Value, out var unix))
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(unix);

            return new ResolvedSource(
                Url: videoUrl,
                Format: SourceFormat.Mp4,
                Headers: null,
                Subtitles: null,
                Qualities: new[] { new QualityVariant(mirror.QualityLabel, videoUrl, null) },
                ExpiresAt: expiresAt,
                ProxyRequired: true, // token IP-bound → el front pega vía nuestro proxy
                Hoster: Hoster);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ResolverException(Hoster, ResolverFailureReason.Timeout, "Streamtape resolve timeout");
        }
        catch (ResolverException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ResolverException(Hoster, ResolverFailureReason.NetworkError,
                $"Streamtape resolve failed: {ex.Message}", ex);
        }
    }
}
