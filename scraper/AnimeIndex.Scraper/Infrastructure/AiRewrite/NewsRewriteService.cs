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
            var result = await gemini.GenerateDetailedAsync(
                SystemInstruction, prompt, settings.UseWebSearch, ct: ct);
            var dto    = ParseJson(result.Text);

            var content = ToContent(dto, item);
            if (content is not null)
            {
                // Modelo y grounding REALES, no los configurados: hasta sep-2026
                // esta línea imprimía settings.Model y websearch=true mientras
                // todos los rewrites corrían en Gemma sin grounding.
                logger.LogInformation(
                    "AiRewrite: rewrote [{Source}] \"{Title}\" via {Model} (websearch={Ws})",
                    item.SourceKey, Truncate(item.Title, 50), result.Model, result.Grounded);
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
        sb.AppendLine("Las slides del posteo muestran el titular + key_points (frases cortas). El caption es TEXTO APARTE:");
        sb.AppendLine("Instagram permite hasta 2200 caracteres, así que el caption tiene que AHONDAR más que las slides — no repetirlas.");
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
              "hook": "3 a 6 PALABRAS para el primer frame del video, en tipografía gigante. Máx 30 caracteres. Nombrá la obra o el hecho concreto — nada de ganchos vacíos tipo 'no vas a creer esto'. Sin punto final. Escribilo normal: el renderer lo pasa a mayúsculas. Ej: 'Jujutsu Kaisen vuelve', 'Free Fire x anime', 'Murió el creador de Berserk'.",
              "cuando": "CUÁNDO pasa lo que anuncia la noticia, corto y en español: '1 de julio 2026', 'enero 2027', 'otoño 2026', 'ya disponible', '20 de noviembre'. SOLO si la fecha aparece en el material de referencia. Si no aparece, null. NO la deduzcas, NO la estimes, NO uses tu conocimiento previo para completarla: una fecha equivocada es peor que ninguna.",
              "donde": "DÓNDE se va a poder ver, corto: 'Crunchyroll', 'Netflix', 'cines de Japón', 'Disney+'. Misma regla que cuando: SOLO si está en el material de referencia, si no null.",
              "headline": "titular original, atractivo, máx ~80 caracteres. Frase completa, SIN puntos suspensivos.",
              "lede": "una sola frase que amplíe el titular, máx ~110 caracteres. Completa, SIN puntos suspensivos.",
              "key_points": ["3 a 5 ideas cortas, autoconclusivas y bien distintas entre sí, máx ~95 caracteres cada una. Cada una es una frase COMPLETA, sin '...' ni recortes. Van en las slides."],
              "caption": "el CUERPO de la publicación, más largo y profundo que las slides: 3 a 5 párrafos cortos que DESARROLLEN la noticia (contexto y antecedentes, detalles como estudio, staff, fechas, plataforma o formato si aparecen, y qué significa para los fans). Tiene que APORTAR datos que NO están en key_points ni en el titular — nada de repetir las frases de las slides. Terminá con una pregunta o CTA. Separá los párrafos con un salto de línea (\\n). Sin hashtags. Sin @.",
              "hashtags": ["6 a 10 hashtags relevantes SIN el símbolo #, en minúscula y sin espacios"]
            }
            """);
        sb.AppendLine();
        sb.AppendLine("IMPORTANTE: key_points (slides) y caption (cuerpo del post) NO pueden decir lo mismo — el caption suma contexto y detalle.");
        sb.AppendLine();
        // "cuando"/"donde" se renderizan como una línea propia sobre el video
        // ("1 DE JULIO 2026 • CRUNCHYROLL"), así que un dato equivocado queda
        // quemado en el reel a la vista de todos. Por eso se insiste acá además
        // del schema: los modelos completan fechas plausibles con demasiada
        // facilidad, y para esto preferimos el hueco antes que el invento.
        sb.AppendLine("SOBRE \"cuando\" y \"donde\": se muestran QUEMADOS sobre el video, así que un dato "
                    + "equivocado queda a la vista de todos. Extraelos del material de referencia y nada más. "
                    + "Si el material no dice la fecha o la plataforma, poné null. Preferimos el hueco al invento.");
        return sb.ToString();
    }

    // ── JSON parsing (tolerant: model may wrap in prose or ``` fences) ───────────

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static RewriteDto? ParseJson(string raw)
    {
        var slice = GeminiClient.ExtractJsonObject(raw);
        if (!slice.StartsWith('{')) return null;

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

        return new NewsContent(headline!, Clean(dto.Lede), keyPoints, caption!, hashtags,
            FromAi: true, Hook: Clean(dto.Hook),
            // Se capan cortos: son una línea sobre el video, no una frase. Un
            // modelo que devuelve "a partir del 1 de julio de 2026 en exclusiva
            // por Crunchyroll" en `cuando` se descarta en vez de romper el layout.
            Cuando: ShortMeta(dto.Cuando, 24), Donde: ShortMeta(dto.Donde, 22));
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

        // Caption = el cuerpo de la noticia, lo más completo que dé el artículo.
        //
        // Antes arrancaba en el párrafo 3 para "no repetir lo que ya se ve en las
        // slides", y eso lo dejaba anémico: con un artículo de 3 párrafos el
        // caption terminaba siendo SOLO el lede. Además desde sep-2026 el reel de
        // tráiler ya no lleva slides de puntos clave (maxKeyPoints: 0), así que
        // esos párrafos no se muestran en ningún lado — saltearlos era tirar la
        // única información que teníamos. Caso real: "Witch on the Holy Night"
        // (10-sep-2026), donde Gemini bloqueó el rewrite por un falso positivo de
        // seguridad y el post salió con dos renglones.
        //
        // Se recorre TODO el artículo desde el párrafo 1 y se corta por
        // presupuesto de caracteres, no por índice.
        var caption = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(lede)) caption.Append(lede);
        for (var i = 1; i < paragraphs.Count && caption.Length < 1200; i++)
        {
            var s = FirstSentence(paragraphs[i], 220);
            if (string.IsNullOrWhiteSpace(s) || s!.Length < 25) continue;
            // Sin repetir el lede ni una frase ya incluida
            if (caption.ToString().Contains(s!, StringComparison.OrdinalIgnoreCase)) continue;
            if (caption.Length > 0 && caption[^1] is not ('.' or '!' or '?')) caption.Append('.');
            caption.Append("\n\n").Append(s);
        }
        if (caption.Length > 0 && caption[^1] is not ('.' or '!' or '?')) caption.Append('.');

        // Cierre SIN promesa falsa. "Te lo contamos completo en SheicobAnime"
        // prometía una nota que no existe: el sitio es un índice de series, no un
        // medio, y el artículo original es de la fuente —que por regla no
        // acreditamos—. Quien tocaba "link en la bio" buscando la nota completa
        // no encontraba nada. Ahora se cierra invitando a comentar, que es lo que
        // sí podemos cumplir y además es señal de ranking.
        caption.Append("\n\n¿Qué opinás? Contanos en los comentarios 👇");

        return new NewsContent(item.Title.Trim(), lede, keyPoints, caption.ToString(), [], FromAi: false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Un dato de meta ("cuando"/"donde") solo sirve si es CORTO: va en una línea
    /// sobre el video junto al otro. Más largo que el cap se descarta entero en
    /// vez de romper el layout o salir recortado a la mitad. También se filtran
    /// los "null"/"n/a" que los modelos devuelven como texto en vez de como null.
    /// Público estático para tests.
    /// </summary>
    public static string? ShortMeta(string? raw, int maxLen)
    {
        var s = Clean(raw)?.TrimEnd('.', ',', ';');
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (NullishText.Contains(s)) return null;
        return s.Length <= maxLen ? s : null;
    }

    private static readonly HashSet<string> NullishText =
        new(StringComparer.OrdinalIgnoreCase)
        { "null", "none", "n/a", "na", "-", "?", "desconocido", "sin fecha", "no especificado", "no especificada" };

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
        [property: JsonPropertyName("hook")]       string? Hook,
        [property: JsonPropertyName("cuando")]     string? Cuando,
        [property: JsonPropertyName("donde")]      string? Donde,
        [property: JsonPropertyName("headline")]   string? Headline,
        [property: JsonPropertyName("lede")]       string? Lede,
        [property: JsonPropertyName("key_points")] List<string>? KeyPoints,
        [property: JsonPropertyName("caption")]    string? Caption,
        [property: JsonPropertyName("hashtags")]   List<string>? Hashtags);
}
