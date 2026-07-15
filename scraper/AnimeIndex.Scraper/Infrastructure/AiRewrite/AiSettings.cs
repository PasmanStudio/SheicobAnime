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
    /// Model id. gemini-2.5-flash-lite is the default: free tier, fast, and more than good
    /// enough to rewrite a news item into structured JSON. OJO: Google recortó la cuota free
    /// tier — visto en prod jul-2026: 429 "limit: 20" requests/día (antes ~1000), con lo que
    /// las últimas corridas del día quedaban sin IA → de ahí el FallbackModel.
    /// (gemini-2.0-flash / 2.0-flash-lite returned free_tier limit:0 for this project.)
    /// Override via Ai__Model for more quality if you have quota.
    /// </summary>
    public string Model { get; set; } = "gemini-2.5-flash-lite";

    /// <summary>
    /// Modelo de RESPALDO cuando el principal devuelve 429 (cuota diaria agotada).
    /// Los Gemma corren sobre una cuota free tier separada y mucho más grande —
    /// no soportan system_instruction, grounding ni JSON mode, el cliente adapta
    /// el payload solo. OJO: Google RENOMBRA los Gemma entre generaciones
    /// (gemma-3-27b-it devolvió 404 en jul-2026 y el fallback quedó muerto);
    /// si este nombre da 404, el cliente descubre el Gemma vigente vía
    /// ListModels solo. Vacío = sin fallback (heurísticas locales).
    /// </summary>
    public string FallbackModel { get; set; } = "gemma-4-31b-it";

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
