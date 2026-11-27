using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>
/// Lo que el prototipo de YouTube necesita para producir un video: datos para
/// YouTube + la lista de escenas. Es el contrato entre la IA (que lo escribe) y
/// el render (que lo consume): se guarda como plan.json, así un plan se puede
/// editar a mano y re-renderizar con <c>--plan</c> sin gastar llamadas a Gemini.
/// </summary>
public sealed class VideoPlan
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public List<VideoScene> Scenes { get; set; } = [];
    public List<string> Sources { get; set; } = [];

    /// <summary>
    /// Videos oficiales de donde sale el metraje (tráileres, teasers, videoclip),
    /// cada uno con su tema. Cada escena muestra planos de la fuente de lo que se
    /// está contando (<see cref="FootageRouter"/>). Null = el tráiler de AniList
    /// (o el de --clip), como antes.
    /// </summary>
    public List<FootageSource>? Footage { get; set; }

    /// <summary>
    /// Tono de la música de fondo: epic | dark | upbeat | chill | emotional (los
    /// moods de la biblioteca de los reels). Lo elige Gemini; null → "dark", que
    /// le queda bien a las teorías.
    /// </summary>
    public string? Mood { get; set; }

    /// <summary>
    /// Cómo debe DECIR la voz los nombres extranjeros ("Jujutsu" → "Yuyutsu"):
    /// la voz clonada lee todo como español y la "j" japonesa sale como jota.
    /// Lo sugiere Gemini por video y lo pisa el glosario youtube/pronunciations.json.
    /// Solo afecta a la voz: los subtítulos muestran la narración original.
    /// </summary>
    public Dictionary<string, string> Pronunciations { get; set; } = [];

    /// <summary>
    /// Todos los nombres y términos extranjeros del guion, aunque se lean bien
    /// tal cual. Con ellos youtube/terms_check.py arma el audio de control.
    /// Otro nombre que el "foreignTerms" del storyboard (objetos {term, lang, say}):
    /// el storyboard se parsea como VideoPlan y con el mismo nombre no deserializaba.
    /// </summary>
    public List<string> CheckTerms { get; set; } = [];

    /// <summary>Qué va en la miniatura (lo propone Gemini con el storyboard). Null en planes viejos: se deduce.</summary>
    public VideoThumbnail? Thumbnail { get; set; }

    /// <summary>
    /// Narración → texto para la voz: reemplaza cada término del glosario como
    /// palabra completa, los más largos primero ("Jujutsu Kaisen" antes que
    /// "Jujutsu"). Público para tests.
    /// </summary>
    public static string ForSpeech(string narration, IReadOnlyDictionary<string, string> pronunciations)
    {
        var text = narration;
        foreach (var (term, say) in pronunciations
                     .Where(p => !p.Key.StartsWith('_') && p.Key.Length > 0 && !string.IsNullOrWhiteSpace(p.Value))
                     .OrderByDescending(p => p.Key.Length))
            text = System.Text.RegularExpressions.Regex.Replace(text,
                $@"(?<![\p{{L}}\p{{N}}]){System.Text.RegularExpressions.Regex.Escape(term)}(?![\p{{L}}\p{{N}}])", say);
        return text;
    }

    /// <summary>Suma el glosario (archivo JSON plano término → pronunciación) con prioridad sobre lo que ya hay.</summary>
    public void MergeGlossary(string? glossaryPath)
    {
        if (glossaryPath is null || !File.Exists(glossaryPath)) return;
        var glossary = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(glossaryPath)) ?? [];
        MergeGlossary(glossary);
    }

    /// <summary>
    /// El glosario manda. Como <see cref="ForSpeech"/> reemplaza primero los términos
    /// más largos, una sugerencia de Gemini que CONTIENE una palabra del glosario
    /// ("Cyberpunk: Edgerunners" con "Edgerunners") la tapaba: esas sugerencias se
    /// descartan y la frase se arma con el glosario. Público para tests.
    /// </summary>
    public void MergeGlossary(IReadOnlyDictionary<string, string> glossary)
    {
        var terms = glossary.Where(g => !g.Key.StartsWith('_') && g.Key.Length > 0).ToList();
        foreach (var suggested in Pronunciations.Keys.ToList())
            if (terms.Any(g => g.Key != suggested && System.Text.RegularExpressions.Regex.IsMatch(suggested,
                    $@"(?<![\p{{L}}\p{{N}}]){System.Text.RegularExpressions.Regex.Escape(g.Key)}(?![\p{{L}}\p{{N}}])")))
                Pronunciations.Remove(suggested);
        foreach (var (term, say) in terms)
            Pronunciations[term] = say;
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static VideoPlan Parse(string json)
    {
        var plan = JsonSerializer.Deserialize<VideoPlan>(json, Json)
            ?? throw new InvalidOperationException("plan vacío");
        plan.Scenes = plan.Scenes.Where(s => !string.IsNullOrWhiteSpace(s.Narration)).ToList();
        plan.StripCallToAction();
        plan.Scenes = plan.Scenes.Where(s => !string.IsNullOrWhiteSpace(s.Narration)).ToList();
        if (plan.Scenes.Count == 0)
            throw new InvalidOperationException("el plan no tiene escenas con narración");
        return plan;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Escenas del final donde se buscan pedidos de like o suscripción.</summary>
    public const int ClosingScenes = 2;

    private static readonly System.Text.RegularExpressions.Regex CallToAction = new(
        @"suscr[ií]b|me gusta|\blikes?\b|campanita|Sheicob",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// El video cierra con una idea o una pregunta, sin "dale me gusta y suscribite
    /// a Sheicob Anime": de eso se ocupa la pantalla final de YouTube (26-nov-2026).
    /// El guion ya lo prohíbe, pero Gemini a veces lo agrega igual: acá se sacan las
    /// frases de las últimas <see cref="ClosingScenes"/> escenas que lo piden. Corre
    /// también al re-renderizar un plan viejo con --plan. Público para tests.
    /// </summary>
    public void StripCallToAction()
    {
        for (var i = Math.Max(0, Scenes.Count - ClosingScenes); i < Scenes.Count; i++)
        {
            var sentences = System.Text.RegularExpressions.Regex.Split(Scenes[i].Narration.Trim(), @"(?<=[.!?…])\s+");
            Scenes[i].Narration = string.Join(" ", sentences.Where(s => !CallToAction.IsMatch(s))).Trim();
        }
    }
}

public sealed class VideoScene
{
    /// <summary>Texto que lee la voz. Español neutro latino.</summary>
    public string Narration { get; set; } = "";

    /// <summary>
    /// "keyart" = arte oficial de AniList (portada o banner); "ai" = imagen
    /// generada con <see cref="ImagePrompt"/>.
    /// </summary>
    public string Visual { get; set; } = "ai";

    /// <summary>Prompt en inglés para el generador de imágenes (solo si Visual = "ai").</summary>
    public string? ImagePrompt { get; set; }

    /// <summary>
    /// Qué tiene que verse mientras se dice esta escena, como búsquedas en inglés
    /// para la wiki de la serie ("Weak Kingsley crown", "Aura Scales of Obedience").
    /// Una por toma de ~3,5 s, en orden. Las resuelve <see cref="VisualSearchService"/>.
    /// </summary>
    public List<string>? VisualQueries { get; set; }

    /// <summary>
    /// Tomas ancladas a la narración (contrato desde el 26-nov-2026): cada una dice
    /// QUÉ se muestra (<see cref="VideoShotSpec.Show"/>, una referencia con tipo) y
    /// DESDE QUÉ PALABRAS (<see cref="VideoShotSpec.At"/>, copiadas de la narración).
    /// El render corta cuando la voz dice esas palabras (tiempos reales de
    /// Scripts/word_times.py). Si está, reemplaza a <see cref="VisualQueries"/>.
    /// </summary>
    public List<VideoShotSpec>? Shots { get; set; }

    /// <summary>
    /// Ids de <see cref="VideoPlan.Footage"/> de donde sale el metraje de esta
    /// escena. Opcional: sin esto se elige por lo que dice la narración. Sirve para
    /// corregir a mano ("no habrá segunda temporada" habla de la primera).
    /// </summary>
    public List<string>? Footage { get; set; }
}

/// <summary>
/// La miniatura: el gancho (1 a 4 palabras que COMPLETAN el título, no lo
/// repiten; la palabra a resaltar entre asteriscos) y los 1 o 2 personajes que
/// van recortados. <see cref="Alternative"/> es el segundo gancho para la variante B.
/// </summary>
public sealed class VideoThumbnail
{
    public string? Text { get; set; }
    public string? Alternative { get; set; }
    public List<string> Characters { get; set; } = [];
}

/// <summary>Una toma anclada: qué se ve y desde qué palabras de la narración.</summary>
public sealed class VideoShotSpec
{
    /// <summary>Las primeras 1 a 4 palabras, copiadas TAL CUAL de la narración, desde donde va la toma.</summary>
    public string At { get; set; } = "";

    /// <summary>
    /// Qué se ve, con prefijo de tipo (ver <see cref="ShotReference"/>):
    /// "poster: Cyberpunk: Edgerunners", "logo: Studio Trigger + CD Projekt Red",
    /// "card: Anime del Año | Crunchyroll Anime Awards 2023", "character: Lucy",
    /// "scene: a city skyline at night with neon lights", "ai: ...", "keyart".
    /// </summary>
    public string Show { get; set; } = "";
}
