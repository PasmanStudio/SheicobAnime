using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>
/// Exporta las métricas de todo lo publicado en Instagram a un CSV.
///
/// Para qué: hasta ahora se publicaba a ciegas — 7 piezas por día, sin ningún
/// dato de vuelta sobre qué funciona. Esto baja las métricas por pieza y las
/// deja cruzables con lo que ya guardamos en la DB (fuente del RSS, titular,
/// formato, hora), que es lo que permite responder preguntas concretas: ¿los
/// reels con tráiler real rinden más que los slideshow? ¿qué fuente tracciona?
/// ¿qué horario?
///
/// Nombres de métricas verificados contra la doc de Meta (sep-2026). Ojo:
/// `impressions` está deprecada para media creada después del 2-jul-2024 y
/// `plays` fue reemplazada por `views` — por eso no se piden.
///
/// PERMISO NECESARIO: el token debe tener `instagram_manage_insights` (Facebook
/// Login) o `instagram_business_manage_insights` (Instagram Login). El token que
/// usamos para publicar puede NO tenerlo: publicar solo necesita
/// `instagram_content_publish`. Si falta, este export lo dice explícitamente en
/// vez de escribir un CSV vacío.
/// </summary>
public class InstagramInsightsService(
    IHttpClientFactory httpClientFactory,
    InstagramSettings settings,
    ILogger<InstagramInsightsService> logger)
{
    private HttpClient Http => httpClientFactory.CreateClient("instagram-graph");
    private string BaseUrl => $"https://graph.facebook.com/{settings.ApiVersion}";

    // `media_product_type` es el campo que distingue REELS de FEED.
    private const string MediaFields =
        "id,media_type,media_product_type,caption,permalink,timestamp,like_count,comments_count";

    // Métricas por tipo. Pedir una métrica no soportada para el tipo hace fallar
    // TODA la llamada, así que las listas van separadas.
    private static readonly string[] ReelMetrics =
    [
        "views", "reach", "likes", "comments", "shares", "saved",
        "total_interactions", "ig_reels_avg_watch_time", "ig_reels_video_view_total_time",
    ];

    private static readonly string[] FeedMetrics =
    [
        "views", "reach", "likes", "comments", "shares", "saved",
        "total_interactions", "profile_visits", "follows",
    ];

    public record MediaRow(
        string Id,
        string MediaType,
        string ProductType,
        string Permalink,
        DateTimeOffset Timestamp,
        string Caption,
        Dictionary<string, long> Metrics);

    /// <summary>Trae todo el media publicado desde <paramref name="since"/>, paginando.</summary>
    public async Task<List<MediaRow>> ExportAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var rows = new List<MediaRow>();
        var url = $"{BaseUrl}/{settings.IgUserId}/media"
                + $"?fields={MediaFields}&limit=50&access_token={settings.AccessToken}";

        var pages = 0;
        while (!string.IsNullOrEmpty(url) && pages++ < 40)
        {
            using var resp = await Http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogError("Insights: /media falló ({Status}): {Body}",
                    (int)resp.StatusCode, Truncate(body, 500));
                throw new InvalidOperationException($"Meta /media devolvió {(int)resp.StatusCode}");
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)) break;

            var reachedEnd = false;
            foreach (var m in data.EnumerateArray())
            {
                var ts = m.TryGetProperty("timestamp", out var t)
                    && DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed : DateTimeOffset.MinValue;

                if (ts < since) { reachedEnd = true; continue; }

                var id = m.GetProperty("id").GetString()!;
                var productType = Str(m, "media_product_type");

                rows.Add(new MediaRow(
                    id,
                    Str(m, "media_type"),
                    productType,
                    Str(m, "permalink"),
                    ts,
                    Str(m, "caption"),
                    await FetchMetricsAsync(id, productType, ct)));
            }

            // Meta devuelve de más nuevo a más viejo: si esta página ya cruzó el
            // corte, no hace falta pedir la siguiente.
            if (reachedEnd) break;
            url = doc.RootElement.TryGetProperty("paging", out var p)
                && p.TryGetProperty("next", out var n) ? n.GetString() ?? "" : "";
        }

        logger.LogInformation("Insights: {Count} piezas exportadas desde {Since:yyyy-MM-dd}", rows.Count, since);
        return rows;
    }

    private async Task<Dictionary<string, long>> FetchMetricsAsync(
        string mediaId, string productType, CancellationToken ct)
    {
        var metrics = productType.Equals("REELS", StringComparison.OrdinalIgnoreCase)
            ? ReelMetrics : FeedMetrics;

        var url = $"{BaseUrl}/{mediaId}/insights"
                + $"?metric={string.Join(",", metrics)}&access_token={settings.AccessToken}";

        var result = new Dictionary<string, long>();
        using var resp = await Http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            // El caso que más importa distinguir: token sin permiso de insights.
            //
            // Meta lo reporta de varias formas según cómo esté autorizada la app.
            // Comprobado en prod el 9-sep-2026: devuelve
            //   (#10) Application does not have permission for this action
            // que viaja como "code":10, NO como el subcode 33 que documenta. Sin
            // cubrir el 10, el export escupía un warning por CADA pieza (cientos
            // de líneas) y recién moría al final, escondiendo la causa real.
            if (body.Contains("error_subcode\":33", StringComparison.Ordinal)
                || body.Contains("\"code\":10", StringComparison.Ordinal)
                || body.Contains("does not have permission", StringComparison.OrdinalIgnoreCase)
                || body.Contains("manage_insights", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "El token de Instagram NO tiene permiso de insights. Listar el media funciona, "
                    + "leer métricas no: son scopes distintos. Hay que re-autorizar la app agregando "
                    + "instagram_manage_insights (Facebook Login) o instagram_business_manage_insights "
                    + "(Instagram Login), y regenerar el secret INSTAGRAM_ACCESS_TOKEN.");
            }
            logger.LogWarning("Insights: media {Id} sin métricas ({Status}): {Body}",
                mediaId, (int)resp.StatusCode, Truncate(body, 240));
            return result;
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return result;

        foreach (var metric in data.EnumerateArray())
        {
            var name = Str(metric, "name");
            if (metric.TryGetProperty("values", out var values)
                && values.ValueKind == JsonValueKind.Array
                && values.GetArrayLength() > 0
                && values[0].TryGetProperty("value", out var v)
                && v.TryGetInt64(out var num))
            {
                result[name] = num;
            }
        }
        return result;
    }

    /// <summary>CSV con una fila por pieza. Las columnas de métricas son la unión de todas.</summary>
    public static string ToCsv(IReadOnlyList<MediaRow> rows)
    {
        var metricNames = rows.SelectMany(r => r.Metrics.Keys).Distinct().OrderBy(x => x).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",",
            new[] { "id", "product_type", "media_type", "published_utc", "hora_art", "permalink", "caption" }
                .Concat(metricNames)));

        foreach (var r in rows.OrderBy(r => r.Timestamp))
        {
            // Hora de Argentina (UTC-3): es la que sirve para decidir horarios.
            var art = r.Timestamp.ToOffset(TimeSpan.FromHours(-3));
            var cells = new List<string>
            {
                r.Id, r.ProductType, r.MediaType,
                r.Timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                art.Hour.ToString(CultureInfo.InvariantCulture),
                r.Permalink,
                Csv(Truncate(r.Caption.ReplaceLineEndings(" "), 90)),
            };
            cells.AddRange(metricNames.Select(m =>
                r.Metrics.TryGetValue(m, out var v) ? v.ToString(CultureInfo.InvariantCulture) : ""));
            sb.AppendLine(string.Join(",", cells));
        }
        return sb.ToString();
    }

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);
}
