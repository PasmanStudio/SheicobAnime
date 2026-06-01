namespace AnimeIndex.Scraper.Infrastructure;

public class AnimeNewsSettings
{
    /// <summary>Maximum news items to post to Instagram per run.</summary>
    public int MaxPerRun { get; set; } = 3;

    /// <summary>Only consider news items published in the last N hours.</summary>
    public int MaxAgeHours { get; set; } = 48;

    /// <summary>
    /// RSS feeds to poll. All feeds must publish in Spanish — no English sources.
    /// Add new feeds via env vars (AnimeNews__Feeds__N__Key etc.) without redeploying.
    /// Every feed must include images in its RSS items; items without images are skipped.
    /// </summary>
    public List<NewsFeedConfig> Feeds { get; set; } =
    [
        // SomosKudasai — principal sitio de noticias anime en español (LATAM)
        // WordPress → RSS con media:content images 1280×720
        new() { Key = "kudasai", Url = "https://somoskudasai.com/feed/" },
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
