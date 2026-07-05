using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AnimeIndex.Api.Data.Entities;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.AiRewrite;

/// <summary>
/// Turns a raw scraped news item into finished, original <see cref="NewsContent"/>.
///
/// Primary path: Gemini rewrites the article in SheicobAnime's own voice (voseo rioplatense,
/// editorial, never copying or crediting the source) and returns structured JSON.
///
/// Fallback path (no API key, or any failure): a clean heuristic builds presentable content
/// from the (already chrome-stripped) summary so the post still goes out — just less polished.
/// </summary>
public class NewsRewriteService(
    AiSettings settings,
    GeminiClient gemini,
    ILogger<NewsRewriteService> logger)
{
    private const string SystemInstruction =
        """
        Sos el editor de SheicobAnime, un sitio de noticias de anime, manga y cultura otaku para toda Latinoamérica.
        Reescribís noticias con voz propia para Instagram. Reglas estrictas:
        - Escribí en español latinoamericano neutro y cercano, entendible en toda Latinoamérica. Tono entusiasta pero claro y editorial, sentence case. EVITÁ modismos muy locales o argentinos (nada de "che", "posta", "un golazo", "armar una banda", "re", "boludo", "pibe").
        - El texto debe ser ORIGINAL, redactado de cero. NUNCA copies frases de la fuente.
        - NUNCA menciones ni acredites a la fuente original, otros sitios, ni textos como "todos los derechos reservados", menús, "publicado por" o nombres de autores ajenos.
        - No inventes datos: si no estás seguro de un dato, no lo afirmes.
        - Nada de clickbait barato ni MAYÚSCULAS sostenidas. Sé claro y atractivo.
        - El caption va sin hashtags y sin el @ de la cuenta (eso se agrega aparte).
        Devolvé EXCLUSIVAMENTE un objeto JSON válido, sin texto adicional ni ```.
        """;

    public async Task<NewsContent> RewriteAsync(AnimeNewsItem item, CancellationToken ct = default)
    {
        if (!settings.IsConfigured)
            return BuildHeuristic(item);

        try
        {
            var prompt = BuildUserPrompt(item);
            var raw    = await gemini.GenerateAsync(SystemInstruction, prompt, settings.UseWebSearch, ct);
            var dto    = ParseJson(raw);

            var content = ToContent(dto, item);
            if (content is not null)
            {
                logger.LogInformation(
                    "AiRewrite: rewrote [{Source}] \"{Title}\" via {Model} (websearch={Ws})",
                    item.SourceKey, Truncate(item.Title, 50), settings.Model, settings.UseWebSearch);
                return content;
            }

            logger.LogWarning("AiRewrite: model output unusable for \"{Title}\" — using heuristic",
                Truncate(item.Title, 50));
        }
        // Solo re-lanza si el CALLER canceló. El timeout de Gemini (HttpClient)
        // llega como TaskCanceledException con token interno cancelado — se traga
        // y cae al heurístico (si no, tumbaba TODO el news pipeline por un timeout).
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "AiRewrite: rewrite failed for \"{Title}\" — using heuristic",
                Truncate(item.Title, 50));
        }

        return BuildHeuristic(item);
    }

    // ── Prompt ─────────────────────────────────────────────────────────────────

    private static string BuildUserPrompt(AnimeNewsItem item)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Reescribí esta noticia de anime/manga para un post de Instagram de SheicobAnime.");
        sb.AppendLine("Si la información es escasa, podés complementarla con contexto público confiable sobre el mismo tema.");
        sb.AppendLine();
        sb.AppendLine($"Título de referencia: {item.Title}");
        if (!string.IsNullOrWhiteSpace(item.Summary))
        {
            sb.AppendLine("Contenido de referencia (resumilo y reescribilo, no lo copies):");
            sb.AppendLine(item.Summary);
        }
        sb.AppendLine();
        sb.AppendLine("Devolvé un JSON con exactamente estas claves:");
        sb.AppendLine("""
            {
              "headline": "titular original, atractivo, máx ~80 caracteres. Frase completa, SIN puntos suspensivos.",
              "lede": "una sola frase que amplíe el titular, máx ~110 caracteres. Completa, SIN puntos suspensivos.",
              "key_points": ["3 a 5 ideas cortas, autoconclusivas y bien distintas entre sí, máx ~95 caracteres cada una. Cada una es una frase COMPLETA, sin '...' ni recortes."],
              "caption": "2 a 4 frases originales con voz rioplatense + una pregunta o CTA al final. Sin hashtags. Sin @.",
              "hashtags": ["6 a 10 hashtags relevantes SIN el símbolo #, en minúscula y sin espacios"]
            }
            """);
        return sb.ToString();
    }

    // ── JSON parsing (tolerant: model may wrap in prose or ``` fences) ───────────

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static RewriteDto? ParseJson(string raw)
    {
        var start = raw.IndexOf('{');
        var end   = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        var slice = raw[start..(end + 1)];
        try { return JsonSerializer.Deserialize<RewriteDto>(slice, JsonOpts); }
        catch { return null; }
    }

    private static NewsContent? ToContent(RewriteDto? dto, AnimeNewsItem item)
    {
        if (dto is null) return null;

        var headline = Clean(dto.Headline);
        var caption  = Clean(dto.Caption);
        if (string.IsNullOrWhiteSpace(headline) || string.IsNullOrWhiteSpace(caption))
            return null;

        var keyPoints = (dto.KeyPoints ?? [])
            .Select(Clean)
            .Where(p => !string.IsNullOrWhiteSpace(p) && p!.Length >= 15)
            .Select(p => p!)
            .Take(5)
            .ToList();

        var hashtags = (dto.Hashtags ?? [])
            .Select(h => Clean(h)?.TrimStart('#').Replace(" ", "").Replace("#", "").ToLowerInvariant())
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h!)
            .Distinct()
            .Take(10)
            .ToList();

        return new NewsContent(headline!, Clean(dto.Lede), keyPoints, caption!, hashtags, FromAi: true);
    }

    // ── Heuristic fallback (clean, but not a true rewrite) ───────────────────────

    private static NewsContent BuildHeuristic(AnimeNewsItem item)
    {
        var paragraphs = (item.Summary ?? string.Empty)
            .Split(["\n\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var lede = paragraphs.Count > 0 ? FirstSentence(paragraphs[0], 120) : null;

        var keyPoints = new List<string>();
        for (var i = 1; i < paragraphs.Count && keyPoints.Count < 2; i++)
        {
            var s = FirstSentence(paragraphs[i], 110);
            if (!string.IsNullOrWhiteSpace(s) && s!.Length >= 20) keyPoints.Add(s!);
        }

        // A presentable caption built from the cleaned lede — no source chrome, no copy of the body.
        var caption = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(lede)) caption.Append(lede);
        if (caption.Length > 0 && caption[^1] is not ('.' or '!' or '?')) caption.Append('.');
        caption.Append("\n\n¿Qué opinás? Te lo contamos completo en SheicobAnime.");

        return new NewsContent(item.Title.Trim(), lede, keyPoints, caption.ToString(), [], FromAi: false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().Trim('"').Trim();
        // Drop trailing ellipsis the model sometimes adds — slides auto-fit, so nothing is cut.
        while (true)
        {
            if (t.EndsWith('…')) t = t[..^1].TrimEnd();
            else if (t.EndsWith("...")) t = t[..^3].TrimEnd();
            else break;
        }
        return t.Length == 0 ? null : t;
    }

    private static string? FirstSentence(string? text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();

        // Skip a tiny leading fragment ("ADS.", "PUBLICIDAD.", "Foto:") before the real sentence.
        var firstBreak = t.IndexOfAny(['.', '!', '?', ':']);
        if (firstBreak is > 0 and < 12 && firstBreak + 1 < t.Length)
            t = t[(firstBreak + 1)..].TrimStart();

        var end = t.IndexOfAny(['.', '!', '?']);
        var s   = end > 0 ? t[..(end + 1)] : t;
        if (s.Length > maxLen)
        {
            var slice = s[..maxLen];
            var space = slice.LastIndexOf(' ');
            s = (space > 0 ? slice[..space] : slice).TrimEnd();
        }
        return s;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed record RewriteDto(
        [property: JsonPropertyName("headline")]   string? Headline,
        [property: JsonPropertyName("lede")]       string? Lede,
        [property: JsonPropertyName("key_points")] List<string>? KeyPoints,
        [property: JsonPropertyName("caption")]    string? Caption,
        [property: JsonPropertyName("hashtags")]   List<string>? Hashtags);
}
