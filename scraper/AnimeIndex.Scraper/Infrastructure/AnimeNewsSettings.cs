namespace AnimeIndex.Scraper.Infrastructure;

public class AnimeNewsSettings
{
    /// <summary>
    /// Maximum news items to post per run.
    /// With hourly cron this should be 1-2 — posting too many at once looks like spam.
    /// </summary>
    public int MaxPerRun { get; set; } = 1;

    /// <summary>Only consider news items published in the last N hours.</summary>
    public int MaxAgeHours { get; set; } = 48;

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
