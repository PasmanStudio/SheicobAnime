using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure;

/// <summary>
/// Avisa al frontend (Cloudflare Worker / OpenNext) que purgue su cache de
/// contenido al terminar un scrape, para que series/episodios/mirrors nuevos se
/// vean al instante en vez de esperar el TTL de 60s.
///
/// Pega un POST a {SiteUrl}/api/revalidate con header `x-revalidate-secret`, que
/// del lado del worker llama revalidateTag("content") (ver web/lib/api.ts
/// CONTENT_CACHE y web/app/api/revalidate/route.ts).
///
/// Best-effort: nunca lanza. No-op si falta SiteUrl o Secret (queda solo el TTL).
///
/// Config (env entre [corchetes]):
///   Revalidate:SiteUrl  [REVALIDATE__SITEURL]  — base del sitio (sin esto, no-op)
///   Revalidate:Secret   [REVALIDATE__SECRET]   — debe coincidir con REVALIDATE_SECRET del worker
/// </summary>
public sealed class SiteRevalidationService(
    IHttpClientFactory httpFactory,
    IConfiguration config,
    ILogger<SiteRevalidationService> logger)
{
    private readonly string? _siteUrl = config["Revalidate:SiteUrl"]?.TrimEnd('/');
    private readonly string? _secret = config["Revalidate:Secret"];

    public bool Enabled => !string.IsNullOrWhiteSpace(_siteUrl) && !string.IsNullOrWhiteSpace(_secret);

    /// <summary>Purga el tag dado (default "content"). Best-effort: nunca lanza.</summary>
    public async Task RevalidateAsync(string tag = "content", CancellationToken ct = default)
    {
        if (!Enabled)
        {
            logger.LogDebug("Revalidate: sin SiteUrl/Secret configurado, omito (queda el TTL de 60s)");
            return;
        }

        try
        {
            var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_siteUrl}/api/revalidate");
            req.Headers.TryAddWithoutValidation("x-revalidate-secret", _secret);
            req.Content = new StringContent($"{{\"tag\":\"{tag}\"}}", Encoding.UTF8);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var resp = await client.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                logger.LogInformation("✅ Revalidate: tag '{Tag}' purgado en {SiteUrl}", tag, _siteUrl);
            else
                logger.LogWarning("Revalidate: HTTP {Status} purgando '{Tag}' en {SiteUrl}",
                    (int)resp.StatusCode, tag, _siteUrl);
        }
        // Solo re-lanza si el CALLER canceló. Un timeout de HttpClient llega como
        // TaskCanceledException con su token interno cancelado — NO es cancelación
        // del caller y debe tragarse (si no, tumba el import entero al final).
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Revalidate: fallo al purgar el tag '{Tag}' (no crítico)", tag);
        }
    }
}
