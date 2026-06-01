namespace AnimeIndex.Scraper.Infrastructure;

public class AnimeNewsSettings
{
    /// <summary>Maximum news items to post to Instagram per run.</summary>
    public int MaxPerRun { get; set; } = 3;

    /// <summary>Only consider news items published in the last N hours.</summary>
    public int MaxAgeHours { get; set; } = 48;

    /// <summary>RSS feeds to poll. Configurable so Spanish feeds can be added without redeploying.</summary>
    public List<NewsFeedConfig> Feeds { get; set; } =
    [
        // English — very reliable RSS with article images
        new() { Key = "ann",         DisplayName = "Anime News Network",  Url = "https://www.animenewsnetwork.com/news/rss.xml" },
        new() { Key = "mal",         DisplayName = "MyAnimeList News",    Url = "https://myanimelist.net/rss/news.xml" },
        // Spanish-language sources (WordPress-based, /feed/ endpoint)
        new() { Key = "crunchyroll", DisplayName = "Crunchyroll Noticias", Url = "https://www.crunchyroll.com/es/news/rss" },
    ];

    public bool IsEnabled => Feeds.Count > 0;
}

public class NewsFeedConfig
{
    /// <summary>Short unique identifier stored in the DB (e.g. "ann").</summary>
    public string Key { get; set; } = null!;
    /// <summary>Human-readable name shown on the generated image.</summary>
    public string DisplayName { get; set; } = null!;
    /// <summary>Full RSS feed URL.</summary>
    public string Url { get; set; } = null!;
}
