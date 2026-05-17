using AnimeIndex.Api.Data.Entities;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

public class CaptionGeneratorService(InstagramSettings settings)
{
    private static readonly string[] BaseHashtags =
    [
        "#anime", "#animeespañol", "#animelatino", "#otaku",
        "#animeonline", "#animefan", "#animecommunity", "#sheicobanime"
    ];

    /// <summary>
    /// Generates a caption for a carousel post covering multiple episodes.
    /// Groups items by series to avoid duplication when multiple episodes from the same series run.
    /// </summary>
    public string GenerateCarouselCaption(IReadOnlyList<(Series Series, Episode Episode)> items)
    {
        var lines = new List<string>();

        var header = items.Count == 1
            ? "🎬 ¡Nuevo episodio disponible!"
            : $"🎬 ¡{items.Count} nuevos episodios disponibles!";
        lines.Add(header);
        lines.Add(string.Empty);

        // Episode listing — grouped by series when duplicates exist
        foreach (var (series, episode) in items)
        {
            var epTitle = episode.Title is { Length: > 0 } t ? $" — {TruncateAt(t, 28)}" : string.Empty;
            lines.Add($"📺 {series.Title} · Episodio {episode.EpisodeNumber}{epTitle}");
        }

        lines.Add(string.Empty);
        lines.Add($"▶️ Míralos gratis en SheicobAnime");
        lines.Add($"🔗 Link en bio");
        lines.Add(string.Empty);

        // Collect genre hashtags from all series (deduplicated)
        var genreTags = items
            .SelectMany(x => x.Series.SeriesGenres ?? [])
            .Select(sg => sg.Genre?.Name)
            .Where(n => n is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => "#" + n!.ToLowerInvariant().Replace(" ", ""))
            .Take(4);

        var seriesTags = items
            .Select(x => "#" + x.Series.Slug.Replace("-", "").Replace("_", "").ToLowerInvariant())
            .Distinct();

        var allHashtags = string.Join(" ", BaseHashtags)
            + " " + string.Join(" ", genreTags)
            + " " + string.Join(" ", seriesTags);

        lines.Add(allHashtags.Trim());
        lines.Add(string.Empty);
        lines.Add($"@{settings.Handle}");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Generates the first-comment text with a direct link per episode.
    /// Posted automatically right after the carousel/single post is published.
    /// Links in Instagram comments are tappable on mobile.
    /// </summary>
    public string GenerateEpisodeLinksComment(IReadOnlyList<(Series Series, Episode Episode)> items)
    {
        var lines = new List<string>();
        lines.Add(items.Count == 1 ? "▶️ Ver episodio:" : "▶️ Ver episodios:");
        lines.Add(string.Empty);

        foreach (var (series, episode) in items)
        {
            var url = $"{settings.SiteUrl}/series/{series.Slug}/{episode.EpisodeNumber}";
            lines.Add($"📺 {series.Title} · Ep {episode.EpisodeNumber}");
            lines.Add(url);
            lines.Add(string.Empty);
        }

        return string.Join("\n", lines).TrimEnd();
    }

    private static string TruncateAt(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen].TrimEnd() + "…";
}
