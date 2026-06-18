using System.Globalization;
using System.Text;
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

        // Cross-feed enrichment: when the same story shows up in more than one feed,
        // merge the bodies into one item (richer context for the rewrite) and drop the
        // duplicate so we don't post the same news twice.
        toInsert = MergeCrossFeedDuplicates(toInsert);

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
    ///   - og:image URL (the clean featured image, preferred over in-body images)
    ///   - Article body text, scoped to the article container so site chrome
    ///     (nav menu, breadcrumb, byline, related posts, "© … derechos reservados")
    ///     never leaks in. Up to ~8 real paragraphs.
    /// Returns (null, null) on failure.
    /// </summary>
    private static async Task<(string? imageUrl, string? body)> TryResolveArticleAsync(
        HttpClient http, string articleUrl, CancellationToken ct)
    {
        using var resp = await http.GetAsync(articleUrl, ct);
        if (!resp.IsSuccessStatusCode) return (null, null);

        var html = await resp.Content.ReadAsStringAsync(ct);
        return ParseArticleHtml(html);
    }

    /// <summary>
    /// Pulls (og:image, clean body) out of an article HTML page. The body is scoped to the
    /// article container and stripped of nav/byline/copyright/share chrome — this is the fix
    /// for the garbage captions. Exposed <c>internal</c> so the --images test exercises the
    /// exact same extraction as production.
    /// </summary>
    internal static (string? imageUrl, string? body) ParseArticleHtml(string html)
    {
        // og:image — the regex has two alternations (property-before-content and the reverse),
        // so the URL is in whichever of the two capture groups matched.
        var imageMatch = OgImageRegex().Match(html);
        var imageUrl   = !imageMatch.Success ? null
            : imageMatch.Groups[1].Success ? imageMatch.Groups[1].Value
            : imageMatch.Groups[2].Value;

        // Narrow to the article body BEFORE extracting paragraphs (drops menus, footer, …).
        var articleHtml = IsolateArticleHtml(html);

        var paragraphs = ParagraphRegex()
            .Matches(articleHtml)
            .Select(m => StripHtml(m.Groups[1].Value, 600))
            .Where(p => p is { Length: > 60 })
            .Select(p => CleanParagraph(p!))             // drop glued "ADS" / ad labels
            .Where(p => p.Length > 40
                     && !p.Contains("var ")              // inline JS
                     && !p.Contains("function(")
                     && !p.Contains("Math.")
                     && !p.TrimStart().StartsWith("/*")   // JS block comment
                     && !IsChromeLine(p))                 // nav/byline/copyright/share text
            .Take(8)
            .ToList();

        var body = paragraphs.Count > 0
            ? string.Join("\n\n", paragraphs)
            : null;

        return (imageUrl, body);
    }

    /// <summary>
    /// Collects usable image URLs from an article page: the og:image first, then in-body
    /// &lt;img&gt; sources scoped to the article container, filtering out ads/icons/avatars/logos.
    /// Used to build image-rich carousels. <c>internal</c> so the publisher and test share it.
    /// </summary>
    internal static List<string> ExtractArticleImages(string html)
    {
        var images = new List<string>();

        var og = OgImageRegex().Match(html);
        if (og.Success)
            images.Add(og.Groups[1].Success ? og.Groups[1].Value : og.Groups[2].Value);

        var article = IsolateArticleHtml(html);
        foreach (Match m in ImgTagRegex().Matches(article))
        {
            var src = BestImgSrc(m.Value);
            if (!string.IsNullOrWhiteSpace(src) && IsUsableImage(src))
                images.Add(System.Net.WebUtility.HtmlDecode(src));
        }

        return images
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    /// <summary>Prefers lazy-load data-* attributes over a placeholder src.</summary>
    private static string? BestImgSrc(string imgTag)
    {
        foreach (var attr in (string[])["data-src", "data-lazy-src", "data-original", "src"])
        {
            var m = Regex.Match(imgTag, attr + @"\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (m.Success && !m.Groups[1].Value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return m.Groups[1].Value;
        }
        return null;
    }

    private static readonly string[] JunkImageMarkers =
        ["gravatar", "avatar", "/emoji", "spinner", "placeholder", "logo", "icon", "sprite",
         "/ads/", "doubleclick", "play.google", "googlesyndication", "amazon-adsystem",
         "1x1", "pixel", "blank.", "/lazy"];

    private static bool IsUsableImage(string url)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        var u = url.ToLowerInvariant();
        if (u.EndsWith(".svg") || u.EndsWith(".gif")) return false;     // icons / emojis
        return !JunkImageMarkers.Any(u.Contains);
    }

    // ── Article-body isolation & chrome filtering ────────────────────────────────

    // Strong, unambiguous article-body container classes (WordPress/news themes).
    // Deliberately NO bare "<article>": on some themes that matches a related-posts card
    // near the footer, which would isolate the WRONG region. When no strong marker is found
    // we use the full page and let the >60 + chrome filters do the work.
    private static readonly string[] ContentStartMarkers =
        ["entry-content", "td-post-content", "post-content", "article-content",
         "single-content", "article-body", "post-body", "the-content"];

    // Where the body ends — everything from here on is footer/related/share/comments.
    private static readonly string[] ContentEndMarkers =
        ["id=\"comments", "class=\"comments", "related-posts", "related_posts", "yarpp",
         "author-box", "post-author", "post-tags", "tags-links", "sharedaddy",
         "jp-relatedposts", "social-share", "newsletter", "wp-block-post-comments",
         "<footer", "id=\"footer", "class=\"footer"];

    /// <summary>Trims the HTML to the article body container when one is recognizable; otherwise
    /// returns the full page (the paragraph filters handle junk in that case).</summary>
    private static string IsolateArticleHtml(string html)
    {
        var start = IndexOfAnyMarker(html, ContentStartMarkers, 0);
        if (start < 0) return html;

        var tagStart = html.LastIndexOf('<', start);
        var body = tagStart >= 0 ? html[tagStart..] : html[start..];

        // Search for the end marker a bit past the start so the container's own attributes
        // (e.g. an "article-content" wrapper) don't immediately match.
        var end = IndexOfAnyMarker(body, ContentEndMarkers, 80);
        return end > 200 ? body[..end] : body;
    }

    /// <summary>Removes a leading inline ad label glued to the text (e.g. "ADS Si entrás…").</summary>
    private static string CleanParagraph(string p) => LeadingAdRegex().Replace(p, string.Empty).TrimStart();

    private static int IndexOfAnyMarker(string haystack, string[] needles, int from)
    {
        var best = -1;
        foreach (var n in needles)
        {
            var i = haystack.IndexOf(n, Math.Min(from, haystack.Length), StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (best < 0 || i < best)) best = i;
        }
        return best;
    }

    /// <summary>True for lines that are site chrome rather than article prose.</summary>
    private static bool IsChromeLine(string p) => ChromeLineRegex().IsMatch(p);

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

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImgTagRegex();

    [GeneratedRegex(@"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']|<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']", RegexOptions.IgnoreCase)]
    private static partial Regex OgImageRegex();

    // Captures content inside <p>...</p> blocks (for article body extraction)
    [GeneratedRegex(@"<p[^>]*>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ParagraphRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRegex();

    // Site chrome that sometimes survives into a paragraph: copyright, breadcrumb, byline,
    // "appeared first on", share/subscribe calls. Matched against a single cleaned line.
    [GeneratedRegex(
        @"©|todos los derechos|derechos reservados|aparece la entrada|apareci[oó] primero en|fue publicad[oa] (originalmente|primero)|^\s*(inicio|home)\s*[»>]|^\s*(por|escrito por|publicado por|fuente)\s*[:·]|s[ií]guenos|seguinos|suscrib[ií]te|comp[aá]rt(e|i)(lo| en| esta)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ChromeLineRegex();

    // Anything that isn't a latin letter/digit/space — used to tokenize titles after de-accenting.
    [GeneratedRegex(@"[^a-z0-9 ]")]
    private static partial Regex NonWordRegex();

    // A leading inline ad label that got glued to a paragraph's text.
    [GeneratedRegex(@"^\s*(ADS|ADVERTISEMENT|PUBLICIDAD)\b[\s:.\-–—]*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingAdRegex();

    // ── Cross-feed duplicate merge ───────────────────────────────────────────

    /// <summary>
    /// Collapses items that report the same story from different feeds into a single
    /// enriched item. The kept item gains the other's body (more context for the rewrite)
    /// and an image if it was missing one. Same-feed items are never merged.
    /// </summary>
    private List<RssItem> MergeCrossFeedDuplicates(List<RssItem> items)
    {
        var kept = new List<RssItem>();
        foreach (var item in items)
        {
            var match = kept.FirstOrDefault(k =>
                k.SourceKey != item.SourceKey && TitlesMatch(k.Title, item.Title));

            if (match is null) { kept.Add(item); continue; }

            if (string.IsNullOrWhiteSpace(match.ImageUrl) && !string.IsNullOrWhiteSpace(item.ImageUrl))
                match.ImageUrl = item.ImageUrl;

            if (!string.IsNullOrWhiteSpace(item.Summary))
            {
                var combined = string.IsNullOrWhiteSpace(match.Summary)
                    ? item.Summary!
                    : match.Summary + "\n\n" + item.Summary;
                match.Summary = combined.Length > 3000 ? combined[..3000] : combined;
            }

            logger.LogDebug("AnimeNews: merged cross-feed duplicate \"{Title}\" ({A}+{B})",
                item.Title.Length > 50 ? item.Title[..50] : item.Title, match.SourceKey, item.SourceKey);
        }
        return kept;
    }

    /// <summary>Two titles match when their significant words overlap heavily (Jaccard ≥ 0.5).</summary>
    private static bool TitlesMatch(string a, string b)
    {
        var sa = SignificantWords(a);
        var sb = SignificantWords(b);
        if (sa.Count < 3 || sb.Count < 3) return false;

        var intersect = sa.Count(sb.Contains);
        var union     = sa.Count + sb.Count - intersect;
        return union > 0 && (double)intersect / union >= 0.5;
    }

    private static readonly HashSet<string> TitleStopWords = new(StringComparer.Ordinal)
    {
        "para", "como", "este", "esta", "esto", "esos", "esas", "unos", "unas",
        "desde", "hasta", "sobre", "entre", "cuando", "donde", "porque", "aunque",
        "tras", "sera", "seran", "anime", "manga", "nuevo", "nueva", "tras",
    };

    private static HashSet<string> SignificantWords(string title)
    {
        var norm = RemoveDiacritics(title.ToLowerInvariant());
        return NonWordRegex().Replace(norm, " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4 && !TitleStopWords.Contains(w))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string RemoveDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

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
