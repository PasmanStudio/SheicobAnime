namespace AnimeIndex.Scraper.Infrastructure;

public class AnimeNewsSettings
{
    /// <summary>
    /// Maximum news items to post per run. Con el cron curado (5 corridas/día) cada corrida
    /// publica solo la más relevante del pool → mantener en 1 (subirlo vuelve a spamear).
    /// </summary>
    public int MaxPerRun { get; set; } = 1;

    /// <summary>Only consider news items published in the last N hours.</summary>
    public int MaxAgeHours { get; set; } = 48;

    /// <summary>
    /// Formato forzado de la corrida — lo setea el workflow según el horario del
    /// cron (cada franja publica un formato fijo: 5 reels + 2 carruseles por día).
    ///   "reel" → siempre Reel (sin dedup de 24 h — el cron ya espacia los horarios).
    ///   "post" → nunca Reel (carrusel/imagen común, como siempre).
    ///   vacío u otro valor → automático: máx. un reel por 24 h (corridas manuales
    ///   o entornos sin el env var; era el comportamiento original).
    /// </summary>
    public string RunFormat { get; set; } = string.Empty;

    public bool IsReelRun => string.Equals(RunFormat, "reel", StringComparison.OrdinalIgnoreCase);
    public bool IsPostRun => string.Equals(RunFormat, "post", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maximum key-point (headline) slides between the cover and the closing CTA in a news
    /// carousel. Total slides = 1 (cover) + up to MaxContentSlides + 1 (CTA). With the AI
    /// producing 3–5 key points, 5 gives rich carousels of up to 7 slides. Instagram allows 10.
    /// </summary>
    public int MaxContentSlides { get; set; } = 5;

    /// <summary>
    /// RSS feeds to poll. All feeds must publish in Spanish — no English sources.
    /// Add new feeds via env vars (AnimeNews__Feeds__N__Key etc.) without redeploying.
    /// Feeds without images in their RSS still work: AnimeNewsFeedService resolves
    /// og:image + body from the article page. Items with no resolvable image are skipped.
    /// </summary>
    public List<NewsFeedConfig> Feeds { get; set; } =
    [
        // SomosKudasai — principal sitio de noticias anime en español (LATAM).
        // URL canónica del feed = /noticias/feed/ (200 directo). OJO: /feed/ da 404
        // y /rss hace 301 a http:// (downgrade HTTPS→HTTP que .NET NO sigue por
        // seguridad → fallaba desde CI). RSS 2.0 con media:content 1280×720.
        new() { Key = "kudasai", Url = "https://somoskudasai.com/noticias/feed/" },

        // Anmosugoi — sitio mexicano de anime/manga (LATAM), español.
        // RSS sin imágenes → la portada se resuelve vía og:image de la página del artículo.
        new() { Key = "anmosugoi", Url = "https://anmosugoi.com/feed/" },

        // MangaLatam — noticias de anime/manga (LATAM), español. Blogspot RSS 2.0;
        // la portada se resuelve vía og:image del artículo.
        new() { Key = "mangalatam", Url = "https://www.mangalatam.com/feeds/posts/default?alt=rss" },

        // Crunchyroll Noticias — oficial, español LATAM (es-419). RSS del API
        // service (la URL "bonita" /es/news/rss devuelve la SPA, no XML). Trae
        // media:thumbnail y content:encoded completo; la página del artículo es
        // una SPA de 14KB sin contenido (og:image = favicon 96px, que el gate de
        // calidad de fotos descarta solo). Los tráilers NO vienen embebidos en el
        // RSS — los encuentra la búsqueda de YouTube (el canal "Crunchyroll en
        // Español" sube la versión latina de casi todo).
        new() { Key = "crunchyroll", Url = "https://cr-news-api-service.prd.crunchyrollsvc.com/v1/es-419/rss" },
    ];

    public bool IsEnabled => Feeds.Count > 0;
}

public class NewsFeedConfig
{
    /// <summary>Short unique identifier stored in the DB (e.g. "kudasai").</summary>
    public string Key { get; set; } = null!;
    /// <summary>Full RSS feed URL.</summary>
    public string Url { get; set; } = null!;
}
