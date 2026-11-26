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
    /// Cómo debe DECIR la voz los nombres extranjeros ("Jujutsu" → "Yuyutsu"):
    /// la voz clonada lee todo como español y la "j" japonesa sale como jota.
    /// Lo sugiere Gemini por video y lo pisa el glosario youtube/pronunciations.json.
    /// Solo afecta a la voz: los subtítulos muestran la narración original.
    /// </summary>
    public Dictionary<string, string> Pronunciations { get; set; } = [];

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
        foreach (var (term, say) in glossary.Where(g => !g.Key.StartsWith('_')))
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
        if (plan.Scenes.Count == 0)
            throw new InvalidOperationException("el plan no tiene escenas con narración");
        return plan;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);
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
}
