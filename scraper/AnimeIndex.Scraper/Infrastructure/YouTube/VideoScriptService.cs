using System.Text;
using System.Text.Json;
using AnimeIndex.Scraper.Infrastructure.AiRewrite;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>
/// Fuentes → <see cref="VideoPlan"/> en DOS llamadas a Gemini, sin grounding:
///   1. investigación + guion (la IA lee las fuentes juntas y escribe la narración),
///   2. storyboard + metadatos de YouTube (parte el guion en escenas y elige qué se ve).
/// Una llamada por escena sería caro e innecesario: el storyboard sale entero de una vez.
/// </summary>
public sealed class VideoScriptService(GeminiClient gemini, ILogger<VideoScriptService> logger)
{
    private const int WordsPerMinute = 150;

    private const string Voice = """
        Escribes para "Sheicob Anime", un canal de YouTube de anime para toda Latinoamérica.
        Idioma: español latino NEUTRO, como un doblaje mexicano. Tuteo ("tú", "puedes").
        Prohibido el voseo ("vos", "podés"), los modismos argentinos o españoles ("che",
        "vale", "tío", "flipar") y el inglés innecesario. Tono: cercano, con ritmo, sin
        exagerar. El texto se lee en voz alta: frases cortas, sin listas, sin emojis,
        sin paréntesis ni abreviaturas; los números y años se escriben como se dicen
        cuando ayuda a la lectura.
        """;

    /// <param name="styleExample">
    /// Transcripción de cómo habla el narrador de la voz clonada. La voz se
    /// clona bien, pero la cadencia sale del TEXTO: con un guion literario el
    /// clon suena distinto al original; con uno escrito como el narrador
    /// (preguntas, "sin embargo", "en primer lugar", "de hecho") se parece mucho
    /// más (prueba de oído del 24-nov-2026).
    /// </param>
    /// <param name="wordsPerMinute">Velocidad del narrador: Edge ~150, el clon ~200.</param>
    public async Task<VideoPlan> WriteAsync(
        VideoResearch research, int minutes, bool hasKeyArt, CancellationToken ct,
        string? styleExample = null, int wordsPerMinute = WordsPerMinute)
    {
        if (research.Sources.Count == 0)
            throw new InvalidOperationException("Sin fuentes no se escribe guion: la IA inventaría los datos.");

        var words = minutes * wordsPerMinute;
        var style = string.IsNullOrWhiteSpace(styleExample) ? "" : $$"""

            ESTILO: escribe como habla este narrador. Copia su forma de construir las frases,
            sus conectores y su ritmo (no su contenido ni sus muletillas exactas en cada frase):
            "{{styleExample.Trim()}}"
            """;

        // ── 1. Investigación + guion ─────────────────────────────────────────
        var sources = new StringBuilder();
        for (var i = 0; i < research.Sources.Count; i++)
        {
            var s = research.Sources[i];
            sources.AppendLine($"### Fuente {i + 1}: {s.Name} ({s.Url})").AppendLine(s.Text).AppendLine();
        }

        var scriptSystem = Voice + """

            Eres investigador y guionista. Usas SOLO los datos de las fuentes que te paso:
            si algo no está en ellas, no lo afirmas. Si las fuentes se contradicen, lo dices.
            Devuelves JSON y nada más.
            """;
        var scriptPrompt = $$"""
            Tema del video: {{research.Topic}}
            Duración objetivo: {{minutes}} minutos (unas {{words}} palabras de narración en total).

            Estructura: un gancho de 2 o 3 frases que haga quedarse a quien mira, el desarrollo
            en orden lógico (contexto, historia, datos curiosos, por qué importa) y un cierre que
            invite a comentar y suscribirse.
            {{style}}

            Devuelve exactamente este JSON:
            {
              "facts": [ { "fact": "dato verificado", "source": 1 } ],
              "script": [ { "section": "gancho|contexto|...|cierre", "narration": "texto a leer" } ]
            }

            FUENTES:
            {{sources}}
            """;

        var script = await gemini.GenerateDetailedAsync(scriptSystem, scriptPrompt, useWebSearch: false, ct: ct);
        var scriptJson = GeminiClient.ExtractJsonObject(script.Text);
        using (var doc = JsonDocument.Parse(scriptJson))
        {
            var narration = doc.RootElement.GetProperty("script").EnumerateArray()
                .Sum(s => (s.GetProperty("narration").GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            logger.LogInformation("Guion ({Model}): {Words} palabras (objetivo {Target})", script.Model, narration, words);
        }

        // ── 2. Storyboard + metadatos ────────────────────────────────────────
        var keyArtRule = hasKeyArt
            ? """Hay arte oficial del anime: usa "visual": "keyart" en el gancho, en el cierre y en hasta 1 de cada 5 escenas del medio."""
            : """No hay arte oficial: todas las escenas usan "visual": "ai".""";

        var boardSystem = Voice + """

            Ahora eres director de video. Recibes un guion y lo conviertes en un storyboard.
            Devuelves JSON y nada más.
            """;
        var boardPrompt = $$"""
            Parte este guion en escenas de 1 a 3 frases (entre 6 y 15 segundos leídas en voz alta).
            Copia la narración TAL CUAL, sin resumir ni agregar nada: la suma de las escenas es el guion completo.

            Para cada escena elige qué se ve. {{keyArtRule}}
            Para "ai" escribe "imagePrompt" EN INGLÉS: una imagen estilo ilustración anime,
            cinematográfica, que ilustre la idea de la escena (lugar, objeto, emoción, época).
            NUNCA nombres de personajes, series, estudios ni personas reales en el prompt, y
            nada de texto dentro de la imagen. Que las escenas seguidas no se repitan.

            Además escribe para YouTube: un título de menos de 70 caracteres que despierte
            curiosidad sin mentir, una descripción de 2 párrafos (sin links) y 8 a 12 tags.

            Devuelve exactamente este JSON:
            {
              "title": "...",
              "description": "...",
              "tags": ["..."],
              "scenes": [ { "narration": "...", "visual": "ai|keyart", "imagePrompt": "..." } ]
            }

            GUION:
            {{scriptJson}}
            """;

        var board = await gemini.GenerateDetailedAsync(boardSystem, boardPrompt, useWebSearch: false, ct: ct);
        var plan = VideoPlan.Parse(GeminiClient.ExtractJsonObject(board.Text));
        plan.Sources = research.Sources.Select(s => s.Url).ToList();
        logger.LogInformation("Storyboard ({Model}): {Scenes} escenas, título \"{Title}\"",
            board.Model, plan.Scenes.Count, plan.Title);
        return plan;
    }
}
