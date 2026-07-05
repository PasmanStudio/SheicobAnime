namespace AnimeIndex.Scraper.Infrastructure.AiRewrite;

/// <summary>
/// The finished, post-ready content for one news item — already original ("our authorship"),
/// already free of any source chrome/copyright. This is what the poster renderer and the
/// caption builder consume; neither touches the raw scraped <c>Summary</c> anymore.
///
/// Produced by <see cref="NewsRewriteService"/>: from Gemini when configured, otherwise from
/// a clean heuristic fallback so the pipeline never breaks.
/// </summary>
public sealed record NewsContent(
    // Punchy original headline for the cover poster (sentence/normal case; the renderer uppercases).
    string Headline,
    // One-line lede shown under the cover title. Null when there's nothing worth adding.
    string? Lede,
    // Short, self-contained ideas — one per carousel slide (typically 2–3). Van en las slides.
    IReadOnlyList<string> KeyPoints,
    // Cuerpo del caption de Instagram: MÁS largo y profundo que las slides (3–5 párrafos con
    // contexto/detalles) y distinto de KeyPoints, cerrando con pregunta/CTA. Sin hashtags ni handle.
    string Caption,
    // Hashtags WITHOUT the leading '#'. Merged with the base set by the caption builder.
    IReadOnlyList<string> Hashtags,
    // True when this came from the AI rewrite (vs. the heuristic fallback). For logging.
    bool FromAi)
{
    public static NewsContent Empty(string headline) =>
        new(headline, null, [], string.Empty, [], FromAi: false);
}
