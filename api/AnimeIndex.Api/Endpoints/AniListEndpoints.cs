using System.Text.Json;
using AnimeIndex.Api.Infrastructure.Cache;

namespace AnimeIndex.Api.Endpoints;

/// <summary>
/// Proxy cacheado de la API GraphQL de AniList para la página /temporada.
///
/// Por qué existe: el frontend corre en Cloudflare Workers y los fetch
/// directos a graphql.anilist.co desde el Worker fallan de forma
/// intermitente (AniList está detrás de Cloudflare con bot-protection;
/// requests datacenter-a-datacenter sin browser headers se bloquean).
/// Render tiene IP estable y acá podemos cachear en Redis — 4 fetches/día
/// en lugar de uno por visita.
/// </summary>
public static class AniListEndpoints
{
    private static readonly string[] ValidSeasons = ["WINTER", "SPRING", "SUMMER", "FALL"];
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

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
        var cached = await cache.GetAsync<JsonElement?>(cacheKey, ct);
        if (cached is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null })
            return Results.Json(cached);

        var logger = loggerFactory.CreateLogger("AniListProxy");
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

            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("AniList devolvió {Status} para {Season} {Year}",
                    (int)resp.StatusCode, season, year);
                return Results.Json(Array.Empty<object>(), statusCode: 200);
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("Page", out var page) ||
                !page.TryGetProperty("media", out var media))
            {
                logger.LogWarning("AniList: respuesta sin data.Page.media para {Season} {Year}", season, year);
                return Results.Json(Array.Empty<object>(), statusCode: 200);
            }

            var clone = media.Clone();
            await cache.SetAsync<JsonElement?>(cacheKey, clone, CacheTtl, ct);
            return Results.Json(clone);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AniList proxy falló para {Season} {Year}", season, year);
            return Results.Json(Array.Empty<object>(), statusCode: 200);
        }
    }
}
