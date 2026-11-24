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
}
