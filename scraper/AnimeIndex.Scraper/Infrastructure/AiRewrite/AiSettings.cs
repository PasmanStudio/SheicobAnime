namespace AnimeIndex.Scraper.Infrastructure.AiRewrite;

/// <summary>
/// Configuration for the AI rewriting layer. Bound from the "Ai" config section
/// (e.g. Ai__ApiKey via env var / GitHub secret GEMINI_API_KEY).
///
/// Provider-agnostic on purpose: today it targets Google Gemini's free tier
/// (generous quota, native JSON output, optional Google Search grounding), but the
/// client is isolated so swapping to Groq / Cloudflare Workers AI later is cheap.
///
/// When ApiKey is empty the whole layer is skipped and the pipeline falls back to
/// clean heuristic content — same best-effort contract as the Instagram publisher.
/// </summary>
public class AiSettings
{
    /// <summary>Gemini API key from https://aistudio.google.com (free, no card). Empty = AI disabled.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Model id. gemini-2.5-flash-lite is the default: free tier with a HIGH daily request
    /// quota (~1000/day vs ~20/day for gemini-2.5-flash), fast, and more than good enough to
    /// rewrite a news item into structured JSON. (gemini-2.0-flash / 2.0-flash-lite returned
    /// free_tier limit:0 for this project.) Override via Ai__Model for more quality if you have quota.
    /// </summary>
    public string Model { get; set; } = "gemini-2.5-flash-lite";

    /// <summary>
    /// Let the model pull extra context from the web (Google Search grounding) when the
    /// source article is thin. Free within the daily grounding quota — fine for ~13 posts/day.
    /// Disable to stay strictly on the provided source text.
    /// </summary>
    public bool UseWebSearch { get; set; } = true;

    /// <summary>Creativity. 0.7 reads natural without drifting from the facts.</summary>
    public float Temperature { get; set; } = 0.7f;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
