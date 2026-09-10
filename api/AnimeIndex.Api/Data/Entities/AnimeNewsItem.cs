namespace AnimeIndex.Api.Data.Entities;

/// <summary>
/// Represents one anime news item fetched from an RSS feed.
/// Tracks both the raw feed data and the Instagram posting status.
/// </summary>
public class AnimeNewsItem
{
    public Guid Id { get; set; }

    /// <summary>Short key identifying the RSS source (e.g. "ann", "mal", "crunchyroll").</summary>
    public string SourceKey { get; set; } = null!;

    /// <summary>RSS item GUID — dedup key per source. Never changes for the same article.</summary>
    public string RssGuid { get; set; } = null!;

    public string Title { get; set; } = null!;

    /// <summary>Short article summary (stripped of HTML tags).</summary>
    public string? Summary { get; set; }

    /// <summary>Best image URL found for this article (media:thumbnail, og:image, etc.).</summary>
    public string? ImageUrl { get; set; }

    public string ArticleUrl { get; set; } = null!;

    public DateTime PublishedAt { get; set; }
    public DateTime FetchedAt { get; set; }

    // ── Instagram posting ─────────────────────────────────────────────────
    // "pending" | "published" | "skipped" | "failed"
    public string IgPostStatus { get; set; } = "pending";
    public string? IgFeedMediaId { get; set; }
    public string? IgStoryMediaId { get; set; }
    /// <summary>IG media id del Reel diario de noticias — null si esta noticia no fue el reel del día.</summary>
    public string? IgReelMediaId { get; set; }
    public DateTime? IgPostedAt { get; set; }
    public string? ErrorMessage { get; set; }

    // ── Métricas del reel (las llena --insights-sync) ─────────────────────
    // Hasta sep-2026 medir costaba una corrida manual de 8 minutos y un CSV, así
    // que se medía cada varios meses y en el medio se publicaba a ciegas. Con
    // esto, cada pregunta futura es una query — y el selector de la noticia del
    // día puede hacer few-shot con nuestros propios resultados en vez de con la
    // intuición genérica del modelo.
    public long? IgReelViews { get; set; }
    public long? IgReelReach { get; set; }
    public long? IgReelShares { get; set; }
    public long? IgReelSaved { get; set; }
    public long? IgReelComments { get; set; }
    public long? IgReelTotalInteractions { get; set; }

    /// <summary>`ig_reels_avg_watch_time` en SEGUNDOS (Meta lo devuelve en ms).</summary>
    public double? IgReelAvgWatchSeconds { get; set; }

    /// <summary>
    /// `reels_skip_rate` en porcentaje. La métrica más accionable que tenemos:
    /// es la única señal de retención independiente de la duración del video.
    /// Meta la reporta de forma despareja (37 de 302 piezas en el export de
    /// sep-2026), así que queda null muy seguido.
    /// </summary>
    public double? IgReelSkipRate { get; set; }

    /// <summary>
    /// Duración RENDERIZADA del reel, en segundos. No viene de la API: la
    /// sabemos exacta al generar el video. Es lo que faltaba para calcular
    /// retención real (watch time ÷ duración) en vez de watch time a secas, que
    /// está acotado por la duración y por eso confundía dos hallazgos distintos.
    /// </summary>
    public double? IgReelDurationSeconds { get; set; }

    /// <summary>Cuándo se sincronizaron las métricas de arriba.</summary>
    public DateTime? IgInsightsAt { get; set; }
}
