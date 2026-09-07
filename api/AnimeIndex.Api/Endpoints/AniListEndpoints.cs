using System.Text.Json;
using AnimeIndex.Api.Infrastructure.Cache;

namespace AnimeIndex.Api.Endpoints;

/// <summary>
/// Proxy cacheado de la API GraphQL de AniList para la página /temporada.
///
/// Por qué existe: el frontend corre en Cloudflare Workers y los fetch
/// directos a graphql.anilist.co desde el Worker fallaban de forma
/// intermitente. Render tiene IP estable y acá podemos cachear en Redis —
/// 4 fetches/día en lugar de uno por visita.
///
/// ESTADO AL 7-sep-2026: AniList devuelve 403 a todo, con el mensaje
/// "The AniList API has been temporarily disabled due to severe stability
/// issues." Verificado desde Render y desde una IP residencial, con y sin
/// User-Agent de browser: no es bot-protection, apagaron la API pública.
/// Mientras dure, /temporada no tiene datos que mostrar y este endpoint
/// devuelve 503 para que el frontend lo diga honestamente en vez de fingir
/// que la temporada está vacía.
/// </summary>
public static class AniListEndpoints
{
    private static readonly string[] ValidSeasons = ["WINTER", "SPRING", "SUMMER", "FALL"];
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    // Cache NEGATIVO: cuando AniList falla, recordarlo un rato.
    //
    // Sin esto cada visita a /temporada dispara un request a AniList que ya
    // sabemos que va a fallar — y con los reintentos del frontend son 3 por
    // visita. 60 s es suficiente para no hacer de amplificador y lo bastante
    // corto para notar la recuperación enseguida.
    private static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(60);

    // AniList pide un User-Agent identificable; HttpClient de .NET no manda
    // ninguno por default. Hoy no es la causa del 403 (ver abajo), pero mandarlo
    // es lo correcto y evita que ESO sea el problema la próxima vez.
    private const string UserAgent = "SheicobAnime/1.0 (+https://sheicobanime.sheicob.workers.dev)";

    // Mismo shape que esperaba el frontend cuando le pegaba directo a AniList
    // (web/src/lib/anilist.ts → AniListMedia).
    private const string SeasonalQuery = """
        query SeasonalAnime($season: MediaSeason, $seasonYear: Int) {
          Page(page: 1, perPage: 50) {
            media(
              season: $season
              seasonYear: $seasonYear
              type: ANIME
              sort: POPULARITY_DESC
              format_in: [TV, TV_SHORT, ONA]
              isAdult: false
            ) {
              id
              title { romaji english native }
              coverImage { extraLarge large color }
              bannerImage
              status
              episodes
              duration
              genres
              averageScore
              popularity
              startDate { year month day }
              studios(isMain: true) { nodes { name } }
              format
            }
          }
        }
        """;

    public static void MapAniListEndpoints(this WebApplication app)
    {
        app.MapGet("/anilist/season/{season}/{year:int}", GetSeason).WithTags("AniList");
    }

    /// <summary>
    /// Un fallo del upstream se reporta COMO fallo, no como "no hay estrenos".
    ///
    /// Antes esto devolvía `200 []` en los tres caminos de error. El frontend no
    /// tenía forma de distinguir "AniList no contesta" de "esta temporada
    /// todavía no tiene títulos anunciados", así que /temporada mostraba
    /// "Todavía no hay información de esta temporada" — le decía al usuario algo
    /// falso sobre la temporada cuando el problema era nuestro.
    ///
    /// Comprobado el 7-sep-2026: AniList responde 403 a TODA request, con
    /// "The AniList API has been temporarily disabled due to severe stability
    /// issues." No es bot-protection ni el User-Agent (pasa igual desde
    /// cualquier IP, con y sin headers de browser): apagaron la API pública. No
    /// hay nada que arreglar de este lado, pero sí hay que decir la verdad.
    /// </summary>
    private static IResult UpstreamUnavailableResult() =>
        Results.Json(
            new { error = "AniList no está disponible", code = "UPSTREAM_UNAVAILABLE" },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> UpstreamUnavailable(
        ICacheService cache, string failureKey, CancellationToken ct)
    {
        await cache.SetAsync<bool?>(failureKey, true, FailureCacheTtl, ct);
        return UpstreamUnavailableResult();
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

    private static async Task<IResult> GetSeason(
        string season,
        int year,
        IHttpClientFactory httpFactory,
        ICacheService cache,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        season = season.ToUpperInvariant();
        if (!ValidSeasons.Contains(season) || year < 1980 || year > 2100)
            return Results.BadRequest(new { error = "season/year inválidos" });

        var cacheKey = $"anilist:season:{season}:{year}";
        var failureKey = $"anilist:season:{season}:{year}:failed";

        var cached = await cache.GetAsync<JsonElement?>(cacheKey, ct);
        if (cached is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null })
            return Results.Json(cached);

        var logger = loggerFactory.CreateLogger("AniListProxy");

        // Falló hace poco: no volver a pegarle a AniList todavía.
        if (await cache.GetAsync<bool?>(failureKey, ct) == true)
            return UpstreamUnavailableResult();

        try
        {
            var client = httpFactory.CreateClient("probe");
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co")
            {
                Content = JsonContent.Create(new
                {
                    query = SeasonalQuery,
                    variables = new { season, seasonYear = year },
                }),
            };
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                logger.LogWarning("AniList devolvió {Status} para {Season} {Year}: {Body}",
                    (int)resp.StatusCode, season, year, Truncate(body, 300));
                return await UpstreamUnavailable(cache, failureKey, ct);
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("Page", out var page) ||
                !page.TryGetProperty("media", out var media))
            {
                logger.LogWarning("AniList: respuesta sin data.Page.media para {Season} {Year}", season, year);
                return await UpstreamUnavailable(cache, failureKey, ct);
            }

            var clone = media.Clone();
            await cache.SetAsync<JsonElement?>(cacheKey, clone, CacheTtl, ct);
            return Results.Json(clone);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AniList proxy falló para {Season} {Year}", season, year);
            return await UpstreamUnavailable(cache, failureKey, ct);
        }
    }
}
