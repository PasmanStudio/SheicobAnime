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
    // TODA la llamada — por eso las listas van separadas Y por eso existe el
    // fallback métrica-por-métrica de FetchMetricsAsync.
    //
    // Se piden MÁS de las que la doc de Meta lista como soportadas a propósito:
    // la doc y la realidad ya divergieron una vez (documenta error_subcode 33
    // para falta de permisos y en prod devolvió code 10). Con el fallback, pedir
    // de más no cuesta nada: la métrica que el tipo no soporte simplemente queda
    // vacía en esa fila y se loguea una vez cuál fue.
    private static readonly string[] ReelMetrics =
    [
        "views", "reach", "likes", "comments", "shares", "saved",
        "total_interactions", "ig_reels_avg_watch_time", "ig_reels_video_view_total_time",
        // reels_skip_rate es la contracara del watch time: mide cuántos pasan de
        // largo. Con rho=0,76 entre retención y views (medido el 10-sep-2026),
        // es la métrica más accionable que faltaba.
        "reels_skip_rate",
        // Reposts y crossposting a Facebook: los shares resultaron ser el
        // amplificador (48 reels con ≥10 shares = 62% de todas las views).
        "reposts", "crossposted_views", "facebook_views",
        // La doc de Meta NO las lista para REELS, solo para feed. Se piden igual
        // para comprobarlo contra la API real: sin esto no se puede responder si
        // los reels convierten a seguidores, que es lo único que compone.
        "follows", "profile_visits",
    ];

    private static readonly string[] FeedMetrics =
    [
        "views", "reach", "likes", "comments", "shares", "saved",
        "total_interactions", "profile_visits", "follows",
        "profile_activity", "reposts",
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

    // Métricas que la API rechazó para un product_type dado. Se aprende una sola
    // vez por corrida y después se saltean, así no se paga un round-trip fallido
    // por cada una de las ~800 piezas.
    private readonly Dictionary<string, HashSet<string>> _unsupported = new();

    private async Task<Dictionary<string, long>> FetchMetricsAsync(
        string mediaId, string productType, CancellationToken ct)
    {
        var key = productType.ToUpperInvariant();
        if (!_unsupported.TryGetValue(key, out var skip))
            _unsupported[key] = skip = [];

        var metrics = (key == "REELS" ? ReelMetrics : FeedMetrics)
            .Where(m => !skip.Contains(m))
            .ToArray();
        if (metrics.Length == 0) return new Dictionary<string, long>();

        var result = await RequestMetricsAsync(mediaId, metrics, ct);
        if (result is not null) return result;

        // La llamada combinada falló. Meta no dice CUÁL métrica molestó — tira
        // toda la respuesta — así que se prueba una por una: se queda con las
        // que andan y se anotan las que no para no volver a pedirlas.
        //
        // Esto es lo que permite pedir métricas "de más" sin riesgo: la doc de
        // Meta no lista follows/profile_visits para REELS, pero la única forma
        // seria de saberlo es preguntarle a la API, no a la doc.
        var recovered = new Dictionary<string, long>();
        var rejected = new List<string>();
        foreach (var m in metrics)
        {
            var one = await RequestMetricsAsync(mediaId, [m], ct);
            if (one is null)
            {
                rejected.Add(m);
                if (skip.Add(m))
                    logger.LogInformation(
                        "Insights: la métrica {Metric} no está soportada para {Type} — se saltea el resto de la corrida. Meta dijo: {Error}",
                        m, key, _lastError);
                continue;
            }
            foreach (var kv in one) recovered[kv.Key] = kv.Value;
        }

        // Ninguna métrica funcionó, ni siquiera de a una. Eso ya no es "una
        // métrica rara": es el token. Acá sí se puede afirmar con confianza,
        // porque se probaron todas por separado.
        if (recovered.Count == 0 && rejected.Count == metrics.Length && !_permissionChecked)
        {
            _permissionChecked = true;
            throw new InvalidOperationException(
                "Ninguna métrica de insights funcionó para el media " + mediaId + ". "
                + "Listar el media anda pero leer métricas no, así que es el token: le falta "
                + "instagram_manage_insights (Facebook Login) o instagram_business_manage_insights "
                + "(Instagram Login). Respuesta de Meta: " + _lastError);
        }
        _permissionChecked = true;
        return recovered;
    }

    // Último cuerpo de error de la API, para poder contarlo en vez de adivinar.
    private string _lastError = "";
    private bool _permissionChecked;

    /// <summary>
    /// Pide un conjunto de métricas. Devuelve null si la API rechazó la llamada
    /// (para que el llamador decida si degradar a una por una).
    /// </summary>
    private async Task<Dictionary<string, long>?> RequestMetricsAsync(
        string mediaId, string[] metrics, CancellationToken ct)
    {
        var url = $"{BaseUrl}/{mediaId}/insights"
                + $"?metric={string.Join(",", metrics)}&access_token={settings.AccessToken}";

        var result = new Dictionary<string, long>();
        using var resp = await Http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            // NO se decide acá si es un problema de permisos.
            //
            // La primera versión lo hacía y dio un falso positivo (run
            // 34428545476): el token TENÍA instagram_manage_insights —
            // verificado por --token-scopes en el paso anterior — pero el 400 de
            // una métrica no soportada matcheó la heurística de "sin permiso" y
            // el export murió en la primera pieza culpando al token.
            //
            // Un 400 acá puede ser cualquiera de las dos cosas y desde una sola
            // respuesta no se distinguen bien. Quien puede distinguirlas es
            // FetchMetricsAsync: si NINGUNA métrica funciona ni siquiera pedida
            // de a una, es el token; si algunas andan, era la métrica.
            _lastError = Truncate(body, 400);
            return null;
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

    /// <summary>
    /// Insights de CUENTA por día: seguidores nuevos, alcance, visitas al perfil.
    ///
    /// Existe porque Meta NO permite medir conversión a seguidores por pieza en
    /// reels. Verificado contra la API el 10-sep-2026, con este mensaje textual:
    ///   (#100) The Media Insights API does not support the follows metric
    ///          for this media product type.
    /// Lo mismo para profile_visits. En feed sí están, pero el feed produjo
    /// 1 (un) seguidor en 484 publicaciones, así que la pregunta real —¿los
    /// reels hacen crecer la cuenta?— solo se puede responder a nivel cuenta:
    /// serie diaria de seguidores nuevos, cruzada contra lo que se publicó ese día.
    ///
    /// La API acepta ventanas de 30 días como máximo por llamada, así que se
    /// pagina de a 30.
    /// </summary>
    public async Task<List<Dictionary<string, string>>> ExportAccountDailyAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        var metrics = new[] { "follower_count", "reach", "profile_views" };
        var days = new SortedDictionary<string, Dictionary<string, string>>();

        var cursor = since;
        var now = DateTimeOffset.UtcNow;
        while (cursor < now)
        {
            var until = cursor.AddDays(29) > now ? now : cursor.AddDays(29);
            foreach (var metric in metrics)
            {
                var url = $"{BaseUrl}/{settings.IgUserId}/insights"
                        + $"?metric={metric}&period=day"
                        + $"&since={cursor.ToUnixTimeSeconds()}&until={until.ToUnixTimeSeconds()}"
                        + $"&access_token={settings.AccessToken}";

                using var resp = await Http.GetAsync(url, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    logger.LogWarning("Insights cuenta: {Metric} falló ({Status}): {Body}",
                        metric, (int)resp.StatusCode, Truncate(body, 240));
                    continue;
                }

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data)) continue;
                foreach (var m in data.EnumerateArray())
                {
                    if (!m.TryGetProperty("values", out var values)) continue;
                    foreach (var v in values.EnumerateArray())
                    {
                        var endTime = Str(v, "end_time");
                        if (endTime.Length < 10) continue;
                        var day = endTime[..10];
                        if (!days.TryGetValue(day, out var row))
                            days[day] = row = new Dictionary<string, string> { ["fecha"] = day };
                        if (v.TryGetProperty("value", out var val) && val.TryGetInt64(out var n))
                            row[metric] = n.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
            cursor = until.AddDays(1);
        }

        logger.LogInformation("Insights: {Count} días de métricas de cuenta", days.Count);
        return [.. days.Values];
    }

    public static string AccountCsv(IReadOnlyList<Dictionary<string, string>> rows)
    {
        var cols = new List<string> { "fecha" };
        foreach (var r in rows)
            foreach (var k in r.Keys)
                if (!cols.Contains(k)) cols.Add(k);

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", cols));
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", cols.Select(c => r.TryGetValue(c, out var v) ? v : "")));
        return sb.ToString();
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
                Csv(TruncateText(r.Caption.ReplaceLineEndings(" "), 90)),
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

    /// <summary>
    /// Corta sin partir un emoji al medio.
    ///
    /// `s[..max]` a secas rompió el export del 10-sep-2026 con
    /// EncoderFallbackException sobre \uD83D: los captions de Instagram están
    /// llenos de emojis, que en UTF-16 son PARES de char. Cortar justo entre
    /// los dos deja un surrogate huérfano, que no es texto válido y explota
    /// recién al codificar el archivo — a 785 piezas de terminar.
    /// </summary>
    private static string TruncateText(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? string.Empty;
        // Si el corte cae sobre un high surrogate, su par quedó afuera: retroceder uno.
        var cut = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..cut];
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);
}
