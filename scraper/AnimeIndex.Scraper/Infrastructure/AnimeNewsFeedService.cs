using System.Text.RegularExpressions;
using System.Xml.Linq;
using AnimeIndex.Api.Data;
using AnimeIndex.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure;

/// <summary>
/// Fetches anime news from RSS feeds, strips HTML, resolves article images,
/// and upserts new items into the anime_news_items table.
///
/// Uses System.Xml.Linq — no additional NuGet packages required.
/// Handles both RSS 2.0 (channel/item) and basic Atom (feed/entry) formats.
/// </summary>
public partial class AnimeNewsFeedService(
    AppDbContext db,
    IHttpClientFactory httpFactory,
    AnimeNewsSettings settings,
    ILogger<AnimeNewsFeedService> logger)
{
    // XML namespaces used in RSS media extensions
    private static readonly XNamespace Media   = "http://search.yahoo.com/mrss/";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";
    private static readonly XNamespace Atom    = "http://www.w3.org/2005/Atom";

    /// <summary>
    /// Fetches all configured feeds, deduplicates against the DB, and upserts new items.
    /// Returns the list of newly inserted items (status = "pending").
    /// </summary>
    public async Task<List<AnimeNewsItem>> FetchAndUpsertAsync(CancellationToken ct = default)
    {
        var allFetched  = new List<RssItem>();
        var http = httpFactory.CreateClient("news-rss");

        foreach (var feed in settings.Feeds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var items = await FetchFeedAsync(http, feed, ct);
                allFetched.AddRange(items);
                logger.LogDebug("AnimeNews: fetched {Count} items from {Key}", items.Count, feed.Key);
            }
            catch (Exception ex) when (ex is not TaskCanceledException { CancellationToken.IsCancellationRequested: true })
            {
                logger.LogWarning(ex, "AnimeNews: failed to fetch feed {Key} ({Url})", feed.Key, feed.Url);
            }
        }

        if (allFetched.Count == 0) return [];

        // Dedup against existing DB rows (batch query)
        var incomingKeys = allFetched.Select(i => (i.SourceKey, i.RssGuid)).ToList();
        var existingGuids = await db.AnimeNewsItems
            .Where(n => incomingKeys.Select(k => k.SourceKey).Contains(n.SourceKey))
            .Select(n => new { n.SourceKey, n.RssGuid })
            .ToListAsync(ct);

        var existingSet = existingGuids
            .Select(x => (x.SourceKey, x.RssGuid))
            .ToHashSet();

        var cutoff = DateTime.UtcNow.AddHours(-settings.MaxAgeHours);
        var toInsert = allFetched
            .Where(i => !existingSet.Contains((i.SourceKey, i.RssGuid))
                     && i.PublishedAt >= cutoff)
            .OrderByDescending(i => i.PublishedAt)
            .ToList();

        if (toInsert.Count == 0)
        {
            logger.LogInformation("AnimeNews: no new items found across {Count} feed(s)", settings.Feeds.Count);
            return [];
        }

        // For each new item: fetch the article page once to get:
        //   a) og:image (if not already in RSS)
        //   b) Full article body text (RSS only has a ~200-char excerpt)
        // SomosKudasai publishes ~7 articles/day so this is 7 extra HTTP requests/day — acceptable.
        foreach (var item in toInsert)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var (ogImage, fullBody) = await TryResolveArticleAsync(http, item.ArticleUrl, ct);
                if (string.IsNullOrWhiteSpace(item.ImageUrl) && !string.IsNullOrWhiteSpace(ogImage))
                    item.ImageUrl = ogImage;
                if (!string.IsNullOrWhiteSpace(fullBody))
                    item.Summary = fullBody; // replace RSS excerpt with full article text
            }
            catch (Exception ex) when (ex is not TaskCanceledException { CancellationToken.IsCancellationRequested: true })
            {
                logger.LogDebug(ex, "AnimeNews: could not fetch article page for {Url}", item.ArticleUrl);
            }
        }

        // Items without an image are saved as "skipped" — Instagram posts
        // always need a photo to have visual impact.
        var entities = toInsert.Select(i => new AnimeNewsItem
        {
            SourceKey    = i.SourceKey,
            RssGuid      = i.RssGuid,
            Title        = i.Title,
            Summary      = i.Summary,
            ImageUrl     = i.ImageUrl,
            ArticleUrl   = i.ArticleUrl,
            PublishedAt  = i.PublishedAt,
            FetchedAt    = DateTime.UtcNow,
            IgPostStatus = string.IsNullOrWhiteSpace(i.ImageUrl) ? "skipped" : "pending",
            ErrorMessage = string.IsNullOrWhiteSpace(i.ImageUrl) ? "No image available" : null,
        }).ToList();

        db.AnimeNewsItems.AddRange(entities);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "AnimeNews: inserted {Count} new item(s) from {FeedCount} feed(s)",
            entities.Count, settings.Feeds.Count);

        return entities;
    }

    /// <summary>
    /// Returns pending items that have an image (up to MaxPerRun), ordered by publish date desc.
    /// Items without images are permanently skipped and never returned here.
    /// </summary>
    public async Task<List<AnimeNewsItem>> GetPendingItemsAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddHours(-settings.MaxAgeHours);
        return await db.AnimeNewsItems
            .Where(n => n.IgPostStatus == "pending"
                     && n.PublishedAt >= cutoff
                     && n.ImageUrl != null)
            .OrderByDescending(n => n.PublishedAt)
            .Take(settings.MaxPerRun)
            .ToListAsync(ct);
    }

    // ── RSS parsing ──────────────────────────────────────────────────────────

    private async Task<List<RssItem>> FetchFeedAsync(
        HttpClient http, NewsFeedConfig feed, CancellationToken ct)
    {
        using var resp = await http.GetAsync(feed.Url, ct);
        resp.EnsureSuccessStatusCode();
        var xml = await resp.Content.ReadAsStringAsync(ct);

        // Some feeds have a BOM or whitespace before the XML declaration — trim it.
        xml = xml.TrimStart('﻿', '​', '\r', '\n', ' ');
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AnimeNews: could not parse XML from {Key}", feed.Key);
            return [];
        }

        // Detect RSS vs Atom
        var root = doc.Root;
        if (root is null) return [];

        return root.Name.LocalName == "feed"
            ? ParseAtom(root, feed)
            : ParseRss2(root, feed);
    }

    private static List<RssItem> ParseRss2(XElement root, NewsFeedConfig feed)
    {
        var items = new List<RssItem>();
        foreach (var item in root.Descendants("item"))
        {
            var title    = item.Element("title")?.Value?.Trim();
            var link     = item.Element("link")?.Value?.Trim()
                        ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "link")?.Attribute("href")?.Value;
            var guid     = item.Element("guid")?.Value?.Trim() ?? link;
            var pubDate  = item.Element("pubDate")?.Value;
            var imageUrl = ExtractImageFromItem(item);

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link) || string.IsNullOrWhiteSpace(guid))
                continue;

            // Prefer content:encoded (full article body) over description (excerpt).
            // SomosKudasai and most WordPress feeds include the full HTML in content:encoded.
            var body = item.Element(Content + "encoded")?.Value
                    ?? item.Element("description")?.Value;

            items.Add(new RssItem(
                feed.Key, guid, title,
                StripHtml(body, 2000), imageUrl, link, ParseDate(pubDate)));
        }
        return items;
    }

    private static List<RssItem> ParseAtom(XElement root, NewsFeedConfig feed)
    {
        var items = new List<RssItem>();
        foreach (var entry in root.Elements(Atom + "entry"))
        {
            var title    = entry.Element(Atom + "title")?.Value?.Trim();
            var link     = entry.Elements(Atom + "link")
                               .FirstOrDefault(e => (string?)e.Attribute("rel") != "enclosure")
                               ?.Attribute("href")?.Value?.Trim();
            var guid     = entry.Element(Atom + "id")?.Value?.Trim() ?? link;
            var summary  = entry.Element(Atom + "content")?.Value
                        ?? entry.Element(Atom + "summary")?.Value;
            var updated  = entry.Element(Atom + "updated")?.Value
                        ?? entry.Element(Atom + "published")?.Value;
            var imageUrl = ExtractImageFromItem(entry);

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link) || string.IsNullOrWhiteSpace(guid))
                continue;

            items.Add(new RssItem(
                feed.Key, guid, title,
                StripHtml(summary, 250), imageUrl, link, ParseDate(updated)));
        }
        return items;
    }

    /// <summary>Looks for a usable image URL inside any RSS item element.</summary>
    private static string? ExtractImageFromItem(XElement item)
    {
        // 1. media:thumbnail url="..."
        var mediaThumbnail = item.Elements(Media + "thumbnail")
            .Select(e => (string?)e.Attribute("url"))
            .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
        if (mediaThumbnail is not null) return mediaThumbnail;

        // 2. media:content url="..." medium="image"
        var mediaContent = item.Elements(Media + "content")
            .Where(e => (string?)e.Attribute("medium") is "image" or null
                     && !string.IsNullOrWhiteSpace((string?)e.Attribute("url")))
            .Select(e => (string?)e.Attribute("url"))
            .FirstOrDefault();
        if (mediaContent is not null) return mediaContent;

        // 3. enclosure url="..." type="image/..."
        var enclosure = item.Elements("enclosure")
            .Where(e => ((string?)e.Attribute("type") ?? "").StartsWith("image/"))
            .Select(e => (string?)e.Attribute("url"))
            .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
        if (enclosure is not null) return enclosure;

        // 4. <img> tag inside description/content
        var rawHtml = item.Element("description")?.Value
                   ?? item.Element(Content + "encoded")?.Value
                   ?? item.Element(Atom + "content")?.Value;
        if (!string.IsNullOrWhiteSpace(rawHtml))
        {
            var m = ImgSrcRegex().Match(rawHtml);
            if (m.Success) return m.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// Fetches the article page and extracts:
    ///   - og:image URL (for items that had no image in RSS)
    ///   - Full article body text from &lt;p&gt; tags (up to 2000 chars)
    /// Returns (null, null) on failure.
    /// </summary>
    private static async Task<(string? imageUrl, string? body)> TryResolveArticleAsync(
        HttpClient http, string articleUrl, CancellationToken ct)
    {
        using var resp = await http.GetAsync(articleUrl, ct);
        if (!resp.IsSuccessStatusCode) return (null, null);

        var html = await resp.Content.ReadAsStringAsync(ct);

        // og:image
        var imageMatch = OgImageRegex().Match(html);
        var imageUrl   = imageMatch.Success ? imageMatch.Groups[1].Value : null;

        // Article body: collect all <p> paragraph contents, filter ads/code/short items
        var paragraphs = ParagraphRegex()
            .Matches(html)
            .Select(m => StripHtml(m.Groups[1].Value, 600))
            .Where(p => p is { Length: > 60 }
                     && !p.Contains("var ")          // inline JS
                     && !p.Contains("function(")
                     && !p.Contains("Math.")
                     && !p.TrimStart().StartsWith("/*"))  // JS block comment
            .Take(6)
            .ToList();

        var body = paragraphs.Count > 0
            ? string.Join("\n\n", paragraphs)
            : null;

        return (imageUrl, body);
    }

    // ── Text helpers ─────────────────────────────────────────────────────────

    private static string? StripHtml(string? html, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        // Replace block-level tags with newlines to preserve paragraph structure.
        var text = BlockTagRegex().Replace(html, "\n");
        // Strip remaining inline tags
        text = HtmlTagRegex().Replace(text, string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        // Collapse runs of whitespace within each line, trim blank lines
        var lines = text.Split('\n')
            .Select(l => WhitespaceRegex().Replace(l, " ").Trim())
            .Where(l => l.Length > 0)
            .ToList();
        text = string.Join("\n\n", lines);

        return text.Length <= maxLen ? text : text[..maxLen].TrimEnd() + "…";
    }

    private static DateTime ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DateTime.UtcNow;
        if (DateTimeOffset.TryParse(raw, out var dto)) return dto.UtcDateTime;
        return DateTime.UtcNow;
    }

    [GeneratedRegex(@"<(?:br|p|div|h[1-6]|li|tr|blockquote|section|article)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex(@"<img[^>]+src=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrcRegex();

    [GeneratedRegex(@"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']|<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']", RegexOptions.IgnoreCase)]
    private static partial Regex OgImageRegex();

    // Captures content inside <p>...</p> blocks (for article body extraction)
    [GeneratedRegex(@"<p[^>]*>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ParagraphRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRegex();

    // ── Internal record ──────────────────────────────────────────────────────

    private sealed class RssItem(
        string sourceKey, string rssGuid, string title,
        string? summary, string? imageUrl, string articleUrl, DateTime publishedAt)
    {
        public string SourceKey   { get; } = sourceKey;
        public string RssGuid     { get; } = rssGuid;
        public string Title       { get; } = title;
        public string? Summary    { get; set; } = summary;  // settable: overwritten by full article body
        public string? ImageUrl   { get; set; } = imageUrl;  // settable for og:image fallback
        public string ArticleUrl  { get; } = articleUrl;
        public DateTime PublishedAt { get; } = publishedAt;
    }
}
