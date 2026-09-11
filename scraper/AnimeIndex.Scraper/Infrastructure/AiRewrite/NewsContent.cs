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
    bool FromAi,
    // 3–6 palabras para el PRIMER FRAME del reel, en tipografía gigante. No es un
    // titular corto: el titular tiene ~80 caracteres y se rompe en 3-5 líneas
    // chicas, que es lo contrario de lo que frena un scroll. Opcional — sin él,
    // el renderer deriva las primeras palabras del titular (ver HookTextFor).
    string? Hook = null,

    // CUÁNDO y DÓNDE se puede ver: "1 de julio 2026" + "Crunchyroll". Es la
    // información más práctica que puede dar un reel de noticias y hasta
    // sep-2026 la tirábamos — venía en el artículo y no se renderizaba en ningún
    // lado. Las cuentas grandes del nicho la ponen siempre, en una línea propia
    // ("1 DE JULIO 2026 • CRUNCHYROLL").
    //
    // Las dos son opcionales A PROPÓSITO: una fecha inventada es peor que
    // ninguna, así que el prompt exige que salgan del artículo y que queden en
    // null si no están.
    string? Cuando = null,
    string? Donde = null)
{
    public static NewsContent Empty(string headline) =>
        new(headline, null, [], string.Empty, [], FromAi: false);
}
