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
        string? styleExample = null, int wordsPerMinute = WordsPerMinute,
        IReadOnlyList<string>? comments = null, string? guide = null)
    {
        // youtube/GUIA_VIDEO.md: lo aprendido revisando videos (qué mostrar cuando se
        // nombra algo, etc.). Va a las dos llamadas; crece con cada revisión.
        var guideBlock = string.IsNullOrWhiteSpace(guide) ? "" : "\n\nGUÍA DEL CANAL (obligatoria):\n" + guide.Trim();
        if (research.Sources.Count == 0)
            throw new InvalidOperationException("Sin fuentes no se escribe guion: la IA inventaría los datos.");

        var words = minutes * wordsPerMinute;
        // Lo que comentó la gente en el reel que originó el video: qué preguntan,
        // qué teorías tienen, qué les gustó. Se responde en el guion (sin citar nombres).
        var audience = comments is not { Count: > 0 } ? "" : $$"""

            LO QUE COMENTÓ EL PÚBLICO en el reel que originó este video (ordenado por likes).
            Usa sus dudas, teorías y opiniones para elegir qué explicar y responde las preguntas
            que se repiten, sin citar usuarios ni copiar comentarios textuales:
            {{string.Join("\n", comments.Take(60).Select(c => "- " + c.Replace('\n', ' ')))}}
            """;
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
            Duración objetivo: {{minutes}} minutos: la narración debe sumar COMO MÍNIMO {{words}} palabras
            (ni una menos: un guion corto da un video corto). Si las fuentes no alcanzan para tanto,
            profundiza en el análisis, el contexto de la serie y lo que comenta el público, sin inventar datos.

            Estructura: un gancho de 2 o 3 frases que haga quedarse a quien mira, el desarrollo
            en orden lógico (contexto, historia, datos curiosos, por qué importa, y una teoría o
            análisis propio cuando las fuentes den pie) y un cierre.
            El CIERRE siempre: una pregunta concreta sobre el tema para que la gente comente
            (por ejemplo "¿qué crees tú sobre esta teoría?" o "¿crees que X va a pasar?"), y
            después pedir el like y la suscripción, en una o dos frases naturales.
            {{style}}
            {{audience}}

            Devuelve exactamente este JSON:
            {
              "facts": [ { "fact": "dato verificado", "source": 1 } ],
              "script": [ { "section": "gancho|contexto|...|cierre", "narration": "texto a leer" } ]
            }

            FUENTES:
            {{sources}}
            """;

        var script = await gemini.GenerateDetailedAsync(scriptSystem + guideBlock, scriptPrompt, useWebSearch: false, ct: ct);
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

            Para cada escena decide QUÉ SE VE y CUÁNDO, en "shots": la imagen cambia en el
            momento en que la voz DICE lo que se muestra, no cada tantos segundos. Recorre la
            narración palabra por palabra y crea una toma por cada cosa que se nombra o afirma:
            - "at": las 1 a 4 palabras de la narración desde donde va la toma, copiadas
              EXACTAMENTE (mismas letras, tildes y orden; sin resumir ni traducir). Son las
              palabras que NOMBRAN la cosa ("entre Studio Trigger", "ganó el Anime", "David Martínez").
              La primera toma de la escena va con las primeras palabras de la escena.
            - "show": qué se muestra, con UNO de estos prefijos:
              · "poster: <título oficial>" cuando se nombra una serie, temporada, película o videojuego
                ("poster: Cyberpunk: Edgerunners 2", "poster: Cyberpunk 2077");
              · "logo: <empresa>" cuando se nombra un estudio, empresa o plataforma; si se nombran
                juntas, en una sola toma con "+" ("logo: Studio Trigger + CD Projekt Red");
              · "card: <dato corto> | <contexto> | <poster: o character: opcional>" cuando se dice un
                premio, cifra, fecha, récord o anuncio: el dato en pocas palabras, como placa
                ("card: Anime del Año | Crunchyroll Anime Awards 2023 | poster: Cyberpunk: Edgerunners",
                "card: 10 episodios | Estreno: septiembre de 2022"). Usa SOLO datos que dice la narración;
              · "character: <nombre completo>" cuando se nombra a un personaje ("character: Lucy");
              · "scene: <descripción visual EN INGLÉS>" para lo que no tiene nombre: un lugar, una
                acción, un ambiente. Palabras concretas que se vean (personas, acción, lugar, colores),
                sin nombres propios: se busca entre los planos de los videos oficiales
                ("futuristic city skyline at night", "a man and a woman embracing under neon lights");
              · "ai: <prompt en inglés>" SOLO para una idea abstracta sin imagen real (el paso del
                tiempo, una emoción), como mucho 1 de cada 6 escenas; sin nombres reales ni texto.
            - Ritmo: una toma cada 2 a 4 segundos (unas 3 palabras por segundo). Dos tomas de una
              escena no pueden arrancar a menos de 4 palabras de distancia: si dos cosas se nombran
              juntas, van en una sola toma. Entre menciones, una toma "scene:" que ilustre la frase.
            - "visual": la imagen de respaldo de la escena. {{keyArtRule}}

            Además escribe para YouTube: un título de menos de 70 caracteres que despierte
            curiosidad sin mentir, una descripción de 2 párrafos (sin links) y 8 a 12 tags.

            MÚSICA DE FONDO: en "mood" elige UNO según el tono del video: epic (acción, anuncios
            grandes), dark (teorías, misterio, polémicas), upbeat (curiosidades, comedia),
            chill (slice of life, notas tranquilas), emotional (drama, despedidas, homenajes).

            PRONUNCIACIÓN: la narración la lee una voz sintética que pronuncia TODO como español.
            En "foreignTerms" lista cada nombre o término extranjero de la narración, tal como
            aparece escrito, con su idioma:
            - japonés en romaji ("lang": "ja"): solo el término; la ortografía para la voz la
              calcula el sistema con reglas fijas, NO escribas "say".
            - inglés u otro idioma ("lang": "en", etc.): agrega "say" con cómo lo diría un narrador
              de doblaje latino, escrito con ortografía española y SIN tildes salvo que el acento
              caiga donde el español no lo pondría (Weak Kingsley → Uik Kingsli, Chainsaw → Cheinso,
              Family → Fámili). Si ya se lee bien en español (Netflix, Lucy), "say" igual al término.
            Lista TODOS los nombres y términos extranjeros (personajes, series, lugares, estudios,
            técnicas), aunque se lean bien: con esa lista se arma un audio de control.

            Devuelve exactamente este JSON:
            {
              "title": "...",
              "description": "...",
              "tags": ["..."],
              "mood": "epic|dark|upbeat|chill|emotional",
              "foreignTerms": [ { "term": "Jujutsu Kaisen", "lang": "ja" }, { "term": "Weak Kingsley", "lang": "en", "say": "Uik Kingsli" } ],
              "scenes": [ { "narration": "...", "visual": "ai|keyart",
                            "shots": [ { "at": "palabras exactas", "show": "poster: ...|logo: ...|card: ...|character: ...|scene: ...|ai: ..." } ] } ]
            }

            GUION:
            {{scriptJson}}
            """;

        var board = await gemini.GenerateDetailedAsync(boardSystem + guideBlock, boardPrompt, useWebSearch: false, ct: ct);
        var boardJson = GeminiClient.ExtractJsonObject(board.Text);
        var plan = VideoPlan.Parse(boardJson);
        plan.Sources = research.Sources.Select(s => s.Url).ToList();
        plan.Pronunciations = PronunciationsFrom(boardJson);
        plan.CheckTerms = ForeignTermsFrom(boardJson);
        logger.LogInformation("Storyboard ({Model}): {Scenes} escenas, título \"{Title}\"",
            board.Model, plan.Scenes.Count, plan.Title);
        var shots = plan.Scenes.Sum(s => s.Shots?.Count ?? 0);
        var lost = plan.Scenes.Sum(s => (s.Shots ?? []).Count(sh =>
            ShotTiming.FindAnchor(s.Narration, new double[ShotTiming.Tokens(s.Narration).Count], sh.At) is null));
        logger.LogInformation("Tomas ancladas: {Shots} ({Lost} con palabras que no están en la narración)", shots, lost);
        return plan;
    }

    /// <summary>
    /// "foreignTerms" del storyboard → glosario para la voz. Lo japonés lo
    /// convierte <see cref="JapaneseRespeller"/> (reglas fijas: Gemini
    /// inventaba tildes como "Shinguéki"); lo inglés usa el "say" de Gemini.
    /// Público para tests.
    /// </summary>
    public static Dictionary<string, string> PronunciationsFrom(string storyboardJson)
    {
        var result = new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(storyboardJson);
        if (!doc.RootElement.TryGetProperty("foreignTerms", out var terms) || terms.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var t in terms.EnumerateArray())
        {
            var term = t.TryGetProperty("term", out var tv) ? tv.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(term)) continue;
            var lang = t.TryGetProperty("lang", out var lv) ? lv.GetString() ?? "" : "";
            var say = lang.Equals("ja", StringComparison.OrdinalIgnoreCase)
                ? JapaneseRespeller.Respell(term)
                : t.TryGetProperty("say", out var sv) ? sv.GetString()?.Trim() : null;
            if (!string.IsNullOrEmpty(say) && say != term) result[term] = say;
        }
        return result;
    }

    /// <summary>Todos los términos de "foreignTerms", cambien o no al leerse: la lista del audio de control. Público para tests.</summary>
    public static List<string> ForeignTermsFrom(string storyboardJson)
    {
        using var doc = JsonDocument.Parse(storyboardJson);
        if (!doc.RootElement.TryGetProperty("foreignTerms", out var terms) || terms.ValueKind != JsonValueKind.Array)
            return [];
        return terms.EnumerateArray()
            .Select(t => t.TryGetProperty("term", out var tv) ? tv.GetString()?.Trim() : null)
            .OfType<string>().Where(t => t.Length > 0).Distinct().ToList();
    }
}
