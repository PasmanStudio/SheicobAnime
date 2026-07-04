using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Scraper.Infrastructure.AiRewrite;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>Un track de la biblioteca musical de los Reels.</summary>
/// <param name="Title">Nombre del track.</param>
/// <param name="Url">Audio público (MP3/M4A).</param>
/// <param name="Mood">epic | dark | upbeat | chill | emotional.</param>
/// <param name="StartSeconds">Offset de arranque (saltea intros largas).</param>
/// <param name="Attribution">Línea final del caption; null = sin línea.</param>
public record ReelTrack(
    string Title, string Url, string Mood, int StartSeconds = 0, string? Attribution = null);

/// <summary>
/// Elige la música del Reel según el contenido: Gemini clasifica la serie en un
/// "mood" (con fallback heurístico por géneros cuando no hay API key) y dentro
/// del mood se elige un track determinístico por serie — cada serie mantiene su
/// vibra, series distintas suenan distinto.
///
/// Dos bibliotecas, en orden de preferencia:
///   1. PROPIA (Cloudinary, carpeta "{CloudinaryFolder}/music"): tracks
///      generados por el usuario (p. ej. Suno — se generan UNA vez con los
///      créditos y acá se reusan infinito; Suno no tiene API oficial). El mood
///      se lee del nombre: epic-1.mp3, dark-2.mp3… Subirlos con
///      `dotnet run -- --upload-music ./carpeta`.
///   2. FALLBACK: Kevin MacLeod (incompetech.com), CC BY 4.0 — libre para uso
///      comercial CON atribución (va al caption).
/// NUNCA música comercial/OSTs: Rights Manager de Meta la detecta y da strikes.
/// </summary>
public class ReelMusicService(
    IHttpClientFactory httpClientFactory,
    InstagramSettings settings,
    GeminiClient gemini,
    AiSettings aiSettings,
    ILogger<ReelMusicService> logger)
{
    private static ReelTrack Cc(string title, string url, string mood, int start = 0) =>
        new(title, url, mood, start, $"🎵 {title} — Kevin MacLeod (incompetech.com) · CC BY 4.0");

    /// <summary>Atribución de los tracks propios — flex de marca, no obligación legal.</summary>
    public const string OwnMusicAttribution = "🎵 Música original · SheicobAnime";

    // URLs verificadas 2026-07 (HTTP 200). Si incompetech rota alguna, el
    // publisher degrada a Reel silencioso — nunca rompe la corrida.
    public static readonly IReadOnlyList<ReelTrack> Library =
    [
        // Acción / shonen / batalla
        Cc("Killers",                  "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Killers.mp3",                  "epic", 5),
        Cc("Volatile Reaction",        "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Volatile%20Reaction.mp3",      "epic"),
        Cc("Exhilarate",               "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Exhilarate.mp3",               "epic"),
        // Misterio / terror / psicológico
        Cc("Darkest Child",            "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Darkest%20Child.mp3",          "dark"),
        Cc("Epic Unease",              "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Epic%20Unease.mp3",            "dark"),
        // Comedia / parodia
        Cc("Monkeys Spinning Monkeys", "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Monkeys%20Spinning%20Monkeys.mp3", "upbeat"),
        Cc("Fluffing a Duck",          "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Fluffing%20a%20Duck.mp3",      "upbeat"),
        Cc("Sneaky Snitch",            "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Sneaky%20Snitch.mp3",          "upbeat"),
        // Slice of life / cotidiano
        Cc("Carefree",                 "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Carefree.mp3",                 "chill"),
        Cc("Wallpaper",                "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Wallpaper.mp3",                "chill"),
        Cc("Deliberate Thought",       "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Deliberate%20Thought.mp3",     "chill"),
        // Romance / drama
        Cc("Heartwarming",             "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Heartwarming.mp3",             "emotional"),
        Cc("Anguish",                  "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Anguish.mp3",                  "emotional"),
    ];

    private static readonly string[] ValidMoods = ["epic", "dark", "upbeat", "chill", "emotional"];

    // ── Biblioteca propia en Cloudinary ───────────────────────────────

    private Cloudinary? _cloudinaryClient;
    private Cloudinary CloudinaryClient => _cloudinaryClient ??= new Cloudinary(
        new Account(settings.CloudinaryCloudName, settings.CloudinaryApiKey, settings.CloudinaryApiSecret));

    /// <summary>Carpeta de Cloudinary donde viven los tracks propios.</summary>
    internal string MusicFolder =>
        string.IsNullOrWhiteSpace(settings.CloudinaryFolder) ? "music" : $"{settings.CloudinaryFolder}/music";

    private IReadOnlyList<ReelTrack>? _customLibrary; // cache por corrida (scoped)

    /// <summary>
    /// Biblioteca efectiva: la propia (Cloudinary) si tiene tracks, si no la CC.
    /// El audio en Cloudinary se lista como resource_type=video.
    /// </summary>
    private async Task<IReadOnlyList<ReelTrack>> GetLibraryAsync(CancellationToken ct)
    {
        if (_customLibrary is null)
        {
            _customLibrary = [];
            if (settings.CloudinaryConfigured)
            {
                try
                {
                    var result = await CloudinaryClient.ListResourcesAsync(new ListResourcesByPrefixParams
                    {
                        Prefix       = MusicFolder + "/",
                        Type         = "upload",
                        ResourceType = ResourceType.Video,
                        MaxResults   = 100,
                    }, ct);

                    _customLibrary = (result.Resources ?? [])
                        .Select(r => ParseTrackFromPublicId(r.PublicId, r.SecureUrl?.ToString()))
                        .Where(t => t is not null)
                        .Select(t => t!)
                        .ToList();

                    if (_customLibrary.Count > 0)
                        logger.LogInformation("Biblioteca musical propia: {Count} track(s) en Cloudinary {Folder}",
                            _customLibrary.Count, MusicFolder);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "No se pudo listar la música propia en Cloudinary — uso biblioteca CC");
                }
            }
        }
        return _customLibrary.Count > 0 ? _customLibrary : Library;
    }

    /// <summary>
    /// Convención de nombres: "{mood}-{loquesea}" (epic-1, dark-taiko…). Público
    /// estático para tests. Devuelve null si la URL falta; mood desconocido → chill.
    /// </summary>
    public static ReelTrack? ParseTrackFromPublicId(string publicId, string? secureUrl)
    {
        if (string.IsNullOrWhiteSpace(secureUrl)) return null;

        var name = publicId.Split('/').Last();
        var mood = name.Split('-')[0].Trim().ToLowerInvariant();
        if (!ValidMoods.Contains(mood)) mood = "chill";

        return new ReelTrack(name, secureUrl, mood, 0, OwnMusicAttribution);
    }

    /// <summary>
    /// Sube un archivo de audio local a la carpeta de música propia. El nombre
    /// del archivo (sin extensión) debe empezar con el mood: epic-1.mp3.
    /// Usado por el comando `--upload-music`.
    /// </summary>
    public async Task<string> UploadTrackAsync(string filePath, CancellationToken ct = default)
    {
        if (!settings.CloudinaryConfigured)
            throw new InvalidOperationException("Cloudinary no configurado — seteá Instagram__Cloudinary*");

        var name = Path.GetFileNameWithoutExtension(filePath);
        var mood = name.Split('-')[0].Trim().ToLowerInvariant();
        if (!ValidMoods.Contains(mood))
            throw new ArgumentException(
                $"'{name}': el nombre debe empezar con un mood válido ({string.Join("|", ValidMoods)}), ej. epic-1.mp3");

        var result = await CloudinaryClient.UploadAsync(new VideoUploadParams
        {
            File      = new FileDescription(filePath),
            PublicId  = name,
            Folder    = MusicFolder,
            Overwrite = true,
        }, ct);

        if (result.Error is not null)
            throw new InvalidOperationException($"Cloudinary upload failed: {result.Error.Message}");

        return result.SecureUrl?.ToString()
            ?? throw new InvalidOperationException("Cloudinary response missing secure_url");
    }

    /// <summary>
    /// Picks the track for a series (AI mood → deterministic choice) and
    /// downloads its MP3. Returns null when anything fails — the caller
    /// publishes a silent reel instead.
    /// </summary>
    public async Task<(ReelTrack Track, byte[] Mp3)?> SelectAndDownloadAsync(
        Series series, CancellationToken ct = default)
    {
        try
        {
            var library = await GetLibraryAsync(ct);
            var mood = await ClassifyMoodAsync(series, ct);
            var track = PickTrack(series.Slug, mood, library);
            logger.LogInformation("Reel music for {Series}: mood={Mood} → {Track}",
                series.Title, mood, track.Title);

            var http = httpClientFactory.CreateClient("probe");
            var mp3 = await http.GetByteArrayAsync(track.Url, ct);
            return (track, mp3);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Music selection/download failed for {Series} — reel goes silent", series.Title);
            return null;
        }
    }

    /// <summary>Gemini clasifica; sin API key o ante cualquier error → heurística por géneros.</summary>
    private async Task<string> ClassifyMoodAsync(Series series, CancellationToken ct)
    {
        var genres = series.SeriesGenres?
            .Select(sg => sg.Genre?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList() ?? [];

        if (string.IsNullOrWhiteSpace(aiSettings.ApiKey))
            return HeuristicMood(genres);

        try
        {
            var synopsis = series.Synopsis is { Length: > 300 } s ? s[..300] : series.Synopsis;
            var response = await gemini.GenerateAsync(
                "Sos un music supervisor. Clasificá el anime en UN mood musical para el video promocional. " +
                "Respondé SOLO un JSON: {\"mood\":\"epic|dark|upbeat|chill|emotional\"}",
                $"Anime: {series.Title}\nGéneros: {string.Join(", ", genres)}\nSinopsis: {synopsis}",
                useWebSearch: false, ct);

            using var doc = JsonDocument.Parse(response);
            var mood = doc.RootElement.GetProperty("mood").GetString()?.Trim().ToLowerInvariant();
            if (mood is not null && ValidMoods.Contains(mood)) return mood;

            logger.LogDebug("Gemini returned unknown mood '{Mood}' — using heuristic", mood);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Gemini mood classification failed — using heuristic");
        }
        return HeuristicMood(genres);
    }

    /// <summary>Mapeo género → mood cuando no hay IA. Público para tests.</summary>
    public static string HeuristicMood(IReadOnlyList<string> genres)
    {
        var g = genres.Select(x => x.ToLowerInvariant()).ToHashSet();

        if (g.Overlaps(["horror", "terror", "mystery", "misterio", "thriller", "psychological", "psicológico", "suspenso"]))
            return "dark";
        if (g.Overlaps(["comedy", "comedia", "parody", "parodia", "gag humor"]))
            return "upbeat";
        if (g.Overlaps(["romance", "drama", "shoujo"]))
            return "emotional";
        if (g.Overlaps(["slice of life", "recuentos de la vida", "iyashikei", "música", "music"]))
            return "chill";
        if (g.Overlaps(["action", "acción", "adventure", "aventura", "shounen", "sports", "deportes", "mecha", "fantasy", "fantasía"]))
            return "epic";

        return "chill"; // default suave — no toda serie amerita épica
    }

    /// <summary>
    /// Determinístico por slug: la misma serie siempre suena igual (identidad),
    /// series distintas rotan dentro del mood. Público para tests.
    /// </summary>
    public static ReelTrack PickTrack(string seriesSlug, string mood, IReadOnlyList<ReelTrack>? library = null)
    {
        library ??= Library;
        var candidates = library.Where(t => t.Mood == mood).ToList();
        if (candidates.Count == 0) candidates = [.. library];

        // Hash estable (no GetHashCode — cambia entre corridas de .NET)
        var hash = 0;
        foreach (var c in seriesSlug) hash = unchecked(hash * 31 + c);
        return candidates[Math.Abs(hash) % candidates.Count];
    }
}
