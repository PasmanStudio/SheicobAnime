using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Scraper.Infrastructure.AiRewrite;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>Un track de la biblioteca musical de los Reels.</summary>
/// <param name="Title">Nombre del track (va en la atribución del caption).</param>
/// <param name="Url">MP3 público (incompetech.com — URLs estables, verificadas).</param>
/// <param name="Mood">epic | dark | upbeat | chill | emotional.</param>
/// <param name="StartSeconds">Offset de arranque (saltea intros largas).</param>
public record ReelTrack(string Title, string Url, string Mood, int StartSeconds = 0)
{
    /// <summary>CC BY 4.0 exige atribución — va como línea final del caption.</summary>
    public string Attribution => $"🎵 {Title} — Kevin MacLeod (incompetech.com) · CC BY 4.0";
}

/// <summary>
/// Elige la música del Reel según el contenido: Gemini clasifica la serie en un
/// "mood" (con fallback heurístico por géneros cuando no hay API key) y dentro
/// del mood se elige un track determinístico por serie — cada serie mantiene su
/// vibra, series distintas suenan distinto.
///
/// Biblioteca: Kevin MacLeod (incompetech.com), licencia CC BY 4.0 — libre para
/// uso comercial CON atribución (la agrega el publisher al caption). NUNCA usar
/// música comercial/OSTs: Rights Manager de Meta la detecta y da strikes.
/// </summary>
public class ReelMusicService(
    IHttpClientFactory httpClientFactory,
    GeminiClient gemini,
    AiSettings aiSettings,
    ILogger<ReelMusicService> logger)
{
    // URLs verificadas 2026-07 (HTTP 200). Si incompetech rota alguna, el
    // publisher degrada a Reel silencioso — nunca rompe la corrida.
    public static readonly IReadOnlyList<ReelTrack> Library =
    [
        // Acción / shonen / batalla
        new("Killers",                  "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Killers.mp3",                  "epic", 5),
        new("Volatile Reaction",        "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Volatile%20Reaction.mp3",      "epic"),
        new("Exhilarate",               "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Exhilarate.mp3",               "epic"),
        // Misterio / terror / psicológico
        new("Darkest Child",            "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Darkest%20Child.mp3",          "dark"),
        new("Epic Unease",              "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Epic%20Unease.mp3",            "dark"),
        // Comedia / parodia
        new("Monkeys Spinning Monkeys", "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Monkeys%20Spinning%20Monkeys.mp3", "upbeat"),
        new("Fluffing a Duck",          "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Fluffing%20a%20Duck.mp3",      "upbeat"),
        new("Sneaky Snitch",            "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Sneaky%20Snitch.mp3",          "upbeat"),
        // Slice of life / cotidiano
        new("Carefree",                 "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Carefree.mp3",                 "chill"),
        new("Wallpaper",                "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Wallpaper.mp3",                "chill"),
        new("Deliberate Thought",       "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Deliberate%20Thought.mp3",     "chill"),
        // Romance / drama
        new("Heartwarming",             "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Heartwarming.mp3",             "emotional"),
        new("Anguish",                  "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Anguish.mp3",                  "emotional"),
    ];

    private static readonly string[] ValidMoods = ["epic", "dark", "upbeat", "chill", "emotional"];

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
            var mood = await ClassifyMoodAsync(series, ct);
            var track = PickTrack(series.Slug, mood);
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
    public static ReelTrack PickTrack(string seriesSlug, string mood)
    {
        var candidates = Library.Where(t => t.Mood == mood).ToList();
        if (candidates.Count == 0) candidates = [.. Library];

        // Hash estable (no GetHashCode — cambia entre corridas de .NET)
        var hash = 0;
        foreach (var c in seriesSlug) hash = unchecked(hash * 31 + c);
        return candidates[Math.Abs(hash) % candidates.Count];
    }
}
