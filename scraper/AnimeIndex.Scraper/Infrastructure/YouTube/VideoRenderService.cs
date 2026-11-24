using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.YouTube;

/// <summary>
/// Opciones del render. Todo gratis: Edge TTS o Qwen3-TTS, Pollinations y FFmpeg.
/// Con <see cref="CloneRef"/> la voz sale de Qwen3-TTS imitando esa grabación
/// (necesita <see cref="CloneRefText"/>, su transcripción exacta); sin ella, Edge TTS.
/// </summary>
public sealed record VideoRenderOptions(
    string OutputDir,
    string Voice = "es-MX-JorgeNeural",
    string Rate = "+0%",
    string TtsCommand = "edge-tts",
    string FfmpegPath = "ffmpeg",
    string FfprobePath = "ffprobe",
    string? CloneRef = null,
    string? CloneRefText = null,
    string QwenModel = "1.7B",
    string PythonCommand = "python",
    string QwenBackend = "kaggle",
    string YtDlpCommand = "yt-dlp",
    string MatcherPython = "python",
    double ShotSeconds = 3.5)
{
    public bool UsesClonedVoice => !string.IsNullOrWhiteSpace(CloneRef);
}

/// <summary>Una toma del video: qué escena acompaña, su lugar dentro de la escena y cuándo va.</summary>
public sealed record VideoShot(int Scene, int Slot, double Start, double Duration);

/// <summary>De dónde sale la imagen de una toma.</summary>
public enum ShotSource { SceneImage, TrailerClip, TrailerStill, Portrait }

/// <summary>Tráiler oficial bajado y cortado: tramos usables como clip y fotogramas sueltos.</summary>
public sealed record TrailerPool(string Path, List<(double Start, double Length)> Clips, List<(string Path, double Time)> Stills);

/// <summary>
/// <see cref="VideoPlan"/> → video.mp4 horizontal 1080p, con el ritmo de los
/// Shorts de teorías que funcionan (analizado el 24-nov-2026 sobre uno de
/// "El Psy-Kongroo": voz continua, micropausas de 0,12–0,28 s solo en las comas,
/// imagen que cambia cada 1–1,5 s SIN esperar a la voz, 36% clips del anime):
///
///   VOZ: una sola pista continua. Cada escena se recorta en los bordes y se une
///   a la siguiente con <see cref="SceneGapSeconds"/> de silencio. La voz no se
///   procesa (el usuario probó EQ/compresión y prefirió el sonido original).
///
///   IMAGEN: independiente de la voz. Cada escena se parte en tomas de
///   ~<see cref="VideoRenderOptions.ShotSeconds"/> que rotan entre la imagen de
///   la escena (IA o arte oficial), clips cortos del tráiler oficial y
///   fotogramas del tráiler, con un flash blanco de 0,1 s en algunos cortes.
///
///   SUBTÍTULOS quemados con los tiempos del TTS (o estimados por frase).
///
/// El MP4 queda en la carpeta de salida para revisarlo; acá no se sube nada.
/// Imágenes (raw_NN.jpg) y tráiler ya bajados se reutilizan al re-renderizar.
/// </summary>
public sealed partial class VideoRenderService(
    IHttpClientFactory httpFactory,
    VisualSearchService visuals,
    ILogger<VideoRenderService> logger)
{
    private const int Fps = 30;

    /// <summary>Silencio entre escenas: el rango de las micropausas del Short de referencia.</summary>
    public const double SceneGapSeconds = 0.2;

    /// <summary>Colchón al final del video, después de la última frase.</summary>
    public const double EndTailSeconds = 1.0;

    public async Task<string> RenderAsync(
        VideoPlan plan, VideoResearch? research, VideoRenderOptions opt, CancellationToken ct)
    {
        Directory.CreateDirectory(opt.OutputDir);
        var dir = Path.GetFullPath(opt.OutputDir);
        await File.WriteAllTextAsync(Path.Combine(dir, "plan.json"), plan.ToJson(), ct);

        var keyArt = await DownloadKeyArtAsync(research, dir, ct);
        var subtitles = new List<(string Srt, TimeSpan Offset)>();
        var keyArtTurn = 0;

        // El tráiler se baja y se corta en paralelo con la voz.
        var trailerJob = PrepareTrailerAsync(research, opt, dir, ct);

        // Pollinations da 1 imagen cada 45 s por IP: se generan en segundo plano,
        // en orden, mientras el loop hace voz y segmentos de las escenas previas.
        bool UsesKeyArt(VideoScene s) =>
            s.Visual.Equals("keyart", StringComparison.OrdinalIgnoreCase) && keyArt.Count > 0;
        var images = plan.Scenes
            .Select(_ => new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToList();
        // Tope de imágenes IA por video: a 1 cada 45 s, un video de 8 min con una
        // por escena tardaría ~45 min. Son el último recurso (la wiki va primero).
        var aiBudget = MaxAiImages;
        var imageJob = Task.Run(async () =>
        {
            for (var i = 0; i < plan.Scenes.Count; i++)
            {
                try
                {
                    var wantsAi = !UsesKeyArt(plan.Scenes[i]) && aiBudget > 0;
                    if (wantsAi) aiBudget--;
                    images[i].SetResult(!wantsAi
                        ? null
                        : await GenerateImageAsync(plan.Scenes[i], i, dir, (i + 1).ToString("00", CultureInfo.InvariantCulture), ct));
                }
                catch (Exception ex)
                {
                    images[i].SetException(ex);
                }
            }
        }, ct);

        // Voz clonada: todas las escenas de una, antes del loop (una llamada a la
        // GPU por escena; las ya generadas quedan en caché si se corta la cuota).
        if (opt.UsesClonedVoice)
            await RunQwenTtsAsync(plan, opt, dir, ct);

        // ── 1. Voz: una pista continua ──────────────────────────────────────
        var voiceParts = new List<string>();
        var gaps = new List<double>();
        var spans = new List<double>();
        var elapsed = 0.0;
        for (var i = 0; i < plan.Scenes.Count; i++)
        {
            var scene = plan.Scenes[i];
            var n = (i + 1).ToString("00", CultureInfo.InvariantCulture);
            var rawAudio = Path.Combine(dir, opt.UsesClonedVoice ? $"scene_{n}.wav" : $"scene_{n}.mp3");
            var srt = Path.Combine(dir, $"scene_{n}.srt");
            if (!opt.UsesClonedVoice)
                await RunTtsAsync(opt, scene.Narration, rawAudio, srt, ct);

            // Sin el silencio que deja cada generación en los bordes: es lo que
            // hacía sonar "cortado" el cambio de escena.
            var voice = Path.Combine(dir, $"voice_{n}.wav");
            await RunAsync(opt.FfmpegPath, TrimVoiceArgs(rawAudio, voice), dir, ct);
            var seconds = await ProbeDurationAsync(opt, voice, ct);
            if (opt.UsesClonedVoice)
                await File.WriteAllTextAsync(srt, EstimateSrt(scene.Narration, seconds), ct); // Qwen no da tiempos

            var gap = i == plan.Scenes.Count - 1 ? EndTailSeconds : SceneGapSeconds;
            subtitles.Add((await File.ReadAllTextAsync(srt, ct), TimeSpan.FromSeconds(elapsed)));
            voiceParts.Add(voice);
            gaps.Add(gap);
            spans.Add(seconds + gap);
            elapsed += seconds + gap;
        }
        await RunAsync(opt.FfmpegPath, VoiceTrackArgs(voiceParts, gaps, Path.Combine(dir, "voice.wav")), dir, ct);
        logger.LogInformation("Voz continua: {Scenes} escenas, {Secs:F1}s", plan.Scenes.Count, elapsed);

        // ── 2. Imagen de cada escena → cuadro 1920x1080 ─────────────────────
        var frames = new List<string>();
        for (var i = 0; i < plan.Scenes.Count; i++)
        {
            var scene = plan.Scenes[i];
            var raw = UsesKeyArt(scene) ? keyArt[keyArtTurn++ % keyArt.Count] : await images[i].Task;
            raw ??= keyArt.Count > 0 ? keyArt[i % keyArt.Count] : null;
            var frame = Path.Combine(dir, $"frame_{(i + 1).ToString("00", CultureInfo.InvariantCulture)}.jpg");
            await RunAsync(opt.FfmpegPath, FrameArgs(raw, frame,
                isAiImage: raw is not null && Path.GetFileName(raw).StartsWith("raw_", StringComparison.Ordinal)), dir, ct);
            frames.Add(frame);
        }
        await imageJob;
        var trailer = await trailerJob;

        // ── 3. Tomas: independientes de la voz ─────────────────────────────
        var shots = PlanShots(spans, opt.ShotSeconds);
        var shotFiles = new List<string>();
        // Clips desde el principio del tráiler y fotogramas desde la mitad, y nunca
        // dos tomas seguidas del tráiler a menos de 8 s entre sí: un tráiler muestra
        // la misma escena en varios cortes y salían dos tomas iguales (el árbol de
        // Frieren, 24-nov-2026).
        var clipTurn = 0;
        var stillTurn = (trailer?.Stills.Count ?? 0) / 2;
        double? lastTrailerTime = null;
        var usedTrailerMoments = new HashSet<double>(); // por segmento: clip y fotograma comparten el plano
        string? lastPortrait = null;
        var counts = new Dictionary<ShotSource, int>();
        var matched = 0;
        // Qué fotograma de los videos oficiales muestra cada descripción del storyboard.
        var matches = trailer is null ? [] : await MatchFramesAsync(plan, trailer, opt, dir, ct);
        for (var k = 0; k < shots.Count; k++)
        {
            var shot = shots[k];
            var startFrame = (int)Math.Round(shot.Start * Fps);
            var frameCount = Math.Max(1, (int)Math.Round((shot.Start + shot.Duration) * Fps) - startFrame);
            var flash = k > 0 && k % 4 == 0; // flash blanco cada ~4 cortes, como la referencia
            var source = ChooseShotSource(shot, k, trailer?.Clips.Count ?? 0, trailer?.Stills.Count ?? 0);

            // Lo que dice la toma manda. Si nombra a un personaje: su retrato
            // oficial. Si describe una imagen: el plano de los videos oficiales que
            // CLIP encontró más parecido (como clip si dura lo suficiente).
            string? portrait = null;
            (double Start, double Length) clip = default;
            (string Path, double Time) still = default;
            var resolved = false;
            var queries = plan.Scenes[shot.Scene].VisualQueries;
            var query = queries is { Count: > 0 } ? queries[shot.Slot % queries.Count] : null;
            if (query is not null)
            {
                portrait = await PickPortraitAsync(query, lastPortrait, dir, opt, ct);
                if (portrait is not null)
                {
                    source = ShotSource.Portrait;
                    lastPortrait = portrait;
                    resolved = true;
                }
                else if (matches.TryGetValue(query, out var ranked))
                {
                    foreach (var (idx, score) in ranked)
                    {
                        if (score < MinFrameMatch) break;
                        var time = trailer!.Stills[idx].Time;
                        if (usedTrailerMoments.Contains(time)) continue;
                        usedTrailerMoments.Add(time);
                        lastTrailerTime = time;
                        if (trailer.Clips[idx].Length >= shot.Duration * 0.6)
                        {
                            source = ShotSource.TrailerClip;
                            clip = trailer.Clips[idx];
                        }
                        else
                        {
                            source = ShotSource.TrailerStill;
                            still = trailer.Stills[idx];
                        }
                        resolved = true;
                        matched++;
                        break;
                    }
                }
            }

            // Sin coincidencia: rotación de antes. Un clip sirve si cubre al menos
            // el 60% de la toma (el resto con cámara lenta); si no, un fotograma.
            // Clips y fotogramas se identifican por el centro de su plano, así no
            // se repite un plano como clip y después como fotograma.
            if (!resolved && source == ShotSource.TrailerClip)
            {
                var fits = trailer!.Clips.Where(c => c.Length >= shot.Duration * 0.6).ToList();
                var pick = PickAwayFrom(fits.Select(c => c.Start + c.Length / 2).ToList(), ref clipTurn,
                    lastTrailerTime, used: usedTrailerMoments);
                if (pick >= 0) { clip = fits[pick]; lastTrailerTime = clip.Start + clip.Length / 2; }
                else source = trailer.Stills.Count > 0 ? ShotSource.TrailerStill : ShotSource.SceneImage;
            }
            if (!resolved && source == ShotSource.TrailerStill)
            {
                var pick = PickAwayFrom(trailer!.Stills.Select(s => s.Time).ToList(), ref stillTurn,
                    lastTrailerTime, used: usedTrailerMoments);
                still = trailer.Stills[pick];
                lastTrailerTime = still.Time;
            }
            counts[source] = counts.GetValueOrDefault(source) + 1;

            var output = Path.Combine(dir, $"shot_{(k + 1).ToString("000", CultureInfo.InvariantCulture)}.mp4");
            var args = source switch
            {
                ShotSource.TrailerClip => ClipShotArgs(trailer!.Path, clip.Start, frameCount, flash, output,
                    slowdown: Math.Max(1, shot.Duration / clip.Length)),
                ShotSource.TrailerStill => ImageShotArgs(still.Path, frameCount, k, flash, output),
                ShotSource.Portrait => ImageShotArgs(portrait!, frameCount, k, flash, output),
                _ => ImageShotArgs(frames[shot.Scene], frameCount, k, flash, output),
            };
            await RunAsync(opt.FfmpegPath, args, dir, ct);
            shotFiles.Add(output);
        }
        logger.LogInformation("Tomas: {Total} ({Mix})", shots.Count,
            string.Join(", ", counts.Select(c => $"{c.Key}={c.Value}")));

        // ── 4. Unir tomas + voz + subtítulos ───────────────────────────────
        await File.WriteAllLinesAsync(Path.Combine(dir, "shots.txt"),
            shotFiles.Select(s => $"file '{Path.GetFileName(s)}'"), ct);
        await RunAsync(opt.FfmpegPath,
            ["-y", "-f", "concat", "-safe", "0", "-i", "shots.txt", "-c", "copy", "visuals.mp4"], dir, ct);

        await File.WriteAllTextAsync(Path.Combine(dir, "subs.srt"), MergeSrt(subtitles), new UTF8Encoding(false), ct);
        var final = Path.Combine(dir, "video.mp4");
        await RunAsync(opt.FfmpegPath,
        [
            "-y", "-i", "visuals.mp4", "-i", "voice.wav",
            // Ruta relativa a propósito: en Windows "C:" rompe el parseo del filtro.
            "-vf", "subtitles=subs.srt:force_style='FontName=Arial,FontSize=15,Bold=1,Outline=2,Shadow=0,MarginV=28'",
            "-map", "0:v", "-map", "1:a", "-t", elapsed.ToString("0.###", CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2", "-movflags", "+faststart", "video.mp4",
        ], dir, ct);

        var duration = TimeSpan.FromSeconds(elapsed);
        await File.WriteAllTextAsync(Path.Combine(dir, "youtube.txt"), Metadata(plan, duration), ct);
        File.Copy(frames[0], Path.Combine(dir, "thumbnail.jpg"), overwrite: true);
        logger.LogInformation("Video listo: {Path} ({Duration:mm\\:ss})", final, duration);
        return final;
    }

    /// <summary>Imágenes IA como máximo por video (Pollinations: 1 cada 45 s).</summary>
    public const int MaxAiImages = 10;

    /// <summary>Similitud CLIP mínima para aceptar un fotograma como "lo que se dice".</summary>
    public const float MinFrameMatch = 0.27f;

    /// <summary>
    /// Si la toma nombra a un personaje, su retrato oficial normalizado a
    /// 1920x1080 (fondo desenfocado: los retratos son verticales). No repite el
    /// mismo retrato en dos tomas seguidas. Null si la toma no es un personaje.
    /// </summary>
    private async Task<string?> PickPortraitAsync(string query, string? lastPortrait, string dir,
        VideoRenderOptions opt, CancellationToken ct)
    {
        var candidate = visuals.FindCharacter(query);
        if (candidate is null) return null;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(candidate.Url)))[..12];
        var raw = Path.Combine(dir, $"portrait_{hash}.img");
        var frame = Path.Combine(dir, $"pframe_{hash}.jpg");
        if (frame == lastPortrait) return null;
        try
        {
            if (!File.Exists(raw))
                await File.WriteAllBytesAsync(raw, await httpFactory.CreateClient("youtube-research").GetByteArrayAsync(candidate.Url, ct), ct);
            if (!File.Exists(frame))
                await RunAsync(opt.FfmpegPath, FrameArgs(raw, frame, isAiImage: false), dir, ct);
            return frame;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            logger.LogWarning("Retrato de {Name} no disponible: {Msg}", candidate.Title, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Corre Scripts/frame_match.py (CLIP) con todas las descripciones visuales
    /// del storyboard contra los fotogramas del video fuente. Devuelve, por
    /// descripción, los fotogramas más parecidos (índice en el pool, puntaje).
    /// Si falla (sin torch, sin modelo), el video sigue con la rotación de siempre.
    /// </summary>
    private async Task<Dictionary<string, List<(int Index, float Score)>>> MatchFramesAsync(
        VideoPlan plan, TrailerPool trailer, VideoRenderOptions opt, string dir, CancellationToken ct)
    {
        var queries = plan.Scenes.SelectMany(s => s.VisualQueries ?? [])
            .Where(q => visuals.FindCharacter(q) is null).Distinct().ToList();
        var result = new Dictionary<string, List<(int, float)>>();
        if (queries.Count == 0) return result;

        var job = Path.Combine(dir, "frame_match_job.json");
        var output = Path.Combine(dir, "frame_match.json");
        await File.WriteAllTextAsync(job, System.Text.Json.JsonSerializer.Serialize(
            new { frames = trailer.Stills.Select(s => s.Path), queries }), ct);
        try
        {
            var parts = opt.MatcherPython.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var script = Path.Combine(AppContext.BaseDirectory, "Scripts", "frame_match.py");
            await RunAsync(parts[0], parts.Skip(1).Concat(["-I", script, job, output]), dir, ct, TimeSpan.FromMinutes(15));
            using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(output, ct));
            foreach (var q in doc.RootElement.EnumerateObject())
                result[q.Name] = q.Value.EnumerateArray()
                    .Select(p => (p[0].GetInt32(), (float)p[1].GetDouble())).ToList();
            logger.LogInformation("CLIP: {Queries} descripciones contra {Frames} fotogramas del video fuente",
                queries.Count, trailer.Stills.Count);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or System.Text.Json.JsonException)
        {
            logger.LogWarning("CLIP no disponible ({Msg}) — los clips van en rotación",
                ex.Message.Length > 300 ? ex.Message[..300] : ex.Message);
        }
        return result;
    }

    // ── Voz ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Recorta el silencio de los bordes (umbral -45 dB) con fundidos de 10 ms
    /// para que no haga "click". No toca el sonido de la voz. Mono 24 kHz, la
    /// frecuencia nativa de Qwen3. Público para tests.
    /// </summary>
    public static List<string> TrimVoiceArgs(string input, string output) =>
    [
        "-y", "-i", input, "-af",
        "silenceremove=start_periods=1:start_threshold=-45dB:start_silence=0.02," +
        "areverse,silenceremove=start_periods=1:start_threshold=-45dB:start_silence=0.04," +
        "afade=t=in:d=0.01,areverse,afade=t=in:d=0.01",
        "-ar", "24000", "-ac", "1", output,
    ];

    /// <summary>
    /// Une las voces de todas las escenas en una sola pista, con el silencio
    /// indicado después de cada una. Público para tests.
    /// </summary>
    public static List<string> VoiceTrackArgs(IReadOnlyList<string> parts, IReadOnlyList<double> gaps, string output)
    {
        var args = new List<string> { "-y" };
        foreach (var p in parts) { args.Add("-i"); args.Add(p); }
        var filter = new StringBuilder();
        for (var i = 0; i < parts.Count; i++)
            filter.Append($"[{i}:a]apad=pad_dur={gaps[i].ToString("0.###", CultureInfo.InvariantCulture)}[a{i}];");
        for (var i = 0; i < parts.Count; i++) filter.Append($"[a{i}]");
        filter.Append($"concat=n={parts.Count}:v=0:a=1[out]");
        args.AddRange(["-filter_complex", filter.ToString(), "-map", "[out]", "-ar", "24000", "-ac", "1", output]);
        return args;
    }

    // ── Tomas ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Parte cada escena (voz + pausa) en tomas de ~<paramref name="shotSeconds"/>,
    /// todas del mismo largo dentro de la escena. Las tomas cubren el video sin
    /// huecos ni solapes. Público para tests.
    /// </summary>
    public static List<VideoShot> PlanShots(IReadOnlyList<double> sceneSpans, double shotSeconds)
    {
        var shots = new List<VideoShot>();
        var t = 0.0;
        for (var s = 0; s < sceneSpans.Count; s++)
        {
            var count = Math.Max(1, (int)Math.Round(sceneSpans[s] / shotSeconds));
            var d = sceneSpans[s] / count;
            for (var j = 0; j < count; j++)
                shots.Add(new VideoShot(s, j, t + j * d, d));
            t += sceneSpans[s];
        }
        return shots;
    }

    /// <summary>
    /// Qué se ve en cada toma. La primera de cada escena es siempre su imagen
    /// (la que eligió el storyboard); las demás rotan clip del tráiler →
    /// fotograma del tráiler → imagen de la escena, lo que da ~1/3 de clips como
    /// la referencia. Sin tráiler, todo sale de las imágenes. Público para tests.
    /// </summary>
    public static ShotSource ChooseShotSource(VideoShot shot, int index, int clips, int stills)
    {
        if (shot.Slot == 0) return ShotSource.SceneImage;
        return (shot.Slot % 3) switch
        {
            1 when clips > 0 => ShotSource.TrailerClip,
            2 when stills > 0 => ShotSource.TrailerStill,
            0 when clips > 0 => ShotSource.TrailerClip,
            _ => stills > 0 && index % 2 == 0 ? ShotSource.TrailerStill : ShotSource.SceneImage,
        };
    }

    /// <summary>
    /// Elige el próximo elemento de una lista rotativa (tramos o fotogramas del
    /// tráiler, por su segundo en el tráiler) que esté a más de
    /// <paramref name="minDistance"/> s del último usado. Si ninguno cumple, el
    /// siguiente en la rotación. -1 si la lista está vacía. Público para tests.
    /// </summary>
    public static int PickAwayFrom(IReadOnlyList<double> times, ref int turn, double? last, double minDistance = 8,
        ISet<double>? used = null)
    {
        if (times.Count == 0) return -1;
        // 1º: un momento sin usar y lejos del último; 2º: lejos aunque repita; 3º: el que sigue.
        foreach (var requireUnused in new[] { true, false })
        {
            for (var step = 0; step < times.Count; step++)
            {
                var i = (turn + step) % times.Count;
                if (requireUnused && used?.Contains(times[i]) == true) continue;
                if (last is not null && Math.Abs(times[i] - last.Value) <= minDistance) continue;
                turn = i + 1;
                used?.Add(times[i]);
                return i;
            }
            if (used is null) break;
        }
        var fallback = turn % times.Count;
        turn = fallback + 1;
        return fallback;
    }

    /// <summary>
    /// Imagen fija → toma con movimiento (Ken Burns). Seis movimientos distintos
    /// (acercar, alejar, paneos y acercamientos a un costado) para que la misma
    /// imagen en dos tomas parezca otra. Se escala a 4K antes del zoompan para
    /// que no tiemble. Sin audio: la voz va aparte. Público para tests.
    /// </summary>
    public static List<string> ImageShotArgs(string image, int frames, int variant, bool flash, string output)
    {
        var d = frames.ToString(CultureInfo.InvariantCulture);
        var (z, x, y) = (variant % 6) switch
        {
            0 => ($"1+0.12*on/{d}", "iw/2-(iw/zoom/2)", "ih/2-(ih/zoom/2)"),
            1 => ($"1.15-0.12*on/{d}", "iw/2-(iw/zoom/2)", "ih/2-(ih/zoom/2)"),
            2 => ("1.12", $"(iw-iw/zoom)*on/{d}", "ih/2-(ih/zoom/2)"),
            3 => ("1.12", $"(iw-iw/zoom)*(1-on/{d})", "ih/2-(ih/zoom/2)"),
            4 => ($"1.2+0.1*on/{d}", "iw*0.08", "ih*0.08"),
            _ => ($"1.2+0.1*on/{d}", "iw-iw/zoom-iw*0.08", "ih/2-(ih/zoom/2)"),
        };
        var filter = $"scale=3840:2160:force_original_aspect_ratio=increase,crop=3840:2160," +
                     $"zoompan=z='{z}':x='{x}':y='{y}':d={d}:s=1920x1080:fps={Fps},format=yuv420p" +
                     (flash ? ",fade=t=in:st=0:d=0.1:color=white" : "");
        return
        [
            "-y", "-i", image, "-vf", filter, "-frames:v", d, "-an",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-r", Fps.ToString(CultureInfo.InvariantCulture), output,
        ];
    }

    /// <summary>
    /// Tramo del tráiler → toma de video (sin su audio). Llena el cuadro 16:9.
    /// Público para tests.
    /// </summary>
    public static List<string> ClipShotArgs(string trailer, double start, int frames, bool flash, string output,
        double slowdown = 1)
    {
        var d = frames.ToString(CultureInfo.InvariantCulture);
        var slow = slowdown > 1.001 ? $"setpts={slowdown.ToString("0.###", CultureInfo.InvariantCulture)}*PTS," : "";
        var filter = $"{slow}scale=1920:1080:force_original_aspect_ratio=increase,crop=1920:1080,fps={Fps},format=yuv420p" +
                     (flash ? ",fade=t=in:st=0:d=0.1:color=white" : "");
        return
        [
            "-y", "-ss", start.ToString("0.###", CultureInfo.InvariantCulture), "-i", trailer,
            "-vf", filter, "-frames:v", d, "-an",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-r", Fps.ToString(CultureInfo.InvariantCulture), output,
        ];
    }

    /// <summary>
    /// Baja el tráiler oficial (YouTube, vía yt-dlp) y lo corta por cambios de
    /// plano: los tramos de 1,2 s o más son clips posibles y de cada uno sale un
    /// fotograma. Se descartan los primeros 2 s y los últimos 6 s (logos,
    /// fechas, créditos). Si algo falla, el video sale igual solo con imágenes.
    /// </summary>
    private async Task<TrailerPool?> PrepareTrailerAsync(VideoResearch? research, VideoRenderOptions opt, string dir, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(research?.TrailerUrl)) return null;
        try
        {
            var path = Directory.GetFiles(dir, "trailer.*").FirstOrDefault(f => !f.EndsWith(".part", StringComparison.Ordinal));
            if (path is null)
            {
                var parts = opt.YtDlpCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var ytArgs = parts.Skip(1).Concat(
                [
                    "-q", "--no-warnings", "--no-playlist",
                    "-f", "bv*[ext=mp4][height<=1080]/bv*[height<=1080]/best[height<=1080]",
                    "-o", "trailer.%(ext)s",
                ]).ToList();
                // En GitHub Actions: proxy WARP y cookies (los pone el workflow,
                // igual que en news-cron) y node como respaldo de deno para el
                // "n challenge" de YouTube.
                if (Environment.GetEnvironmentVariable("YTDLP_PROXY") is { Length: > 0 } proxy)
                    ytArgs.AddRange(["--proxy", proxy]);
                if (Environment.GetEnvironmentVariable("YTDLP_COOKIES") is { Length: > 0 } cookies && File.Exists(cookies))
                    ytArgs.AddRange(["--cookies", cookies]);
                if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                    ytArgs.AddRange(["--js-runtimes", "node"]);
                ytArgs.Add(research.TrailerUrl!);
                await RunAsync(parts[0], ytArgs, dir, ct, TimeSpan.FromMinutes(5));
                path = Directory.GetFiles(dir, "trailer.*").First(f => !f.EndsWith(".part", StringComparison.Ordinal));
            }

            var duration = await ProbeDurationAsync(opt, path, ct);
            var (_, err) = await RunFullAsync(opt.FfmpegPath,
                ["-hide_banner", "-i", path, "-vf", "scale=320:-2,select='gt(scene,0.3)',showinfo", "-an", "-f", "null", "-"],
                dir, ct, TimeSpan.FromMinutes(5));
            var cuts = new List<double> { 0 };
            cuts.AddRange(ShowInfoTime().Matches(err).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
            cuts.Add(duration);

            var pool = new TrailerPool(path, [], []);
            var kept = new List<float[]>();
            for (var i = 0; i + 1 < cuts.Count && pool.Clips.Count < 80; i++)
            {
                var (a, b) = (cuts[i] + 0.15, cuts[i + 1] - 0.15);
                // Los créditos del staff (texto en japonés encima de la imagen) se
                // concentran al principio y al final: visto en el tráiler de Frieren.
                if (a < Math.Max(2, duration * 0.15) || b > duration - Math.Max(6, duration * 0.10) || b - a < 1.2) continue;

                var still = Path.Combine(dir, $"trailer_seg_{i:000}.jpg");
                if (!File.Exists(still))
                    await RunAsync(opt.FfmpegPath,
                        ["-y", "-ss", ((a + b) / 2).ToString("0.###", CultureInfo.InvariantCulture), "-i", path,
                         "-frames:v", "1", "-q:v", "2", still], dir, ct);

                // Un tráiler vuelve varias veces al mismo lugar: si este plano se
                // parece a uno ya elegido, se descarta (si no, salen dos tomas iguales).
                var signature = ImageSignature(still);
                if (signature is null || kept.Any(k => SignatureDistance(k, signature) < SimilarSignature)) continue;
                kept.Add(signature);
                pool.Clips.Add((a, b - a));
                pool.Stills.Add((still, (a + b) / 2));
            }
            logger.LogInformation("Tráiler: {Secs:F0}s, {Clips} clips y {Stills} fotogramas usables",
                duration, pool.Clips.Count, pool.Stills.Count);
            return pool.Clips.Count == 0 ? null : pool;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning("Sin tráiler ({Url}): {Msg} — el video sale solo con imágenes",
                research.TrailerUrl, ex.Message.Length > 300 ? ex.Message[..300] : ex.Message);
            return null;
        }
    }

    [GeneratedRegex(@"pts_time:([\d.]+)")]
    private static partial Regex ShowInfoTime();

    /// <summary>Diferencia media (0–255) por debajo de la cual dos planos se consideran el mismo.</summary>
    public const float SimilarSignature = 22f;

    /// <summary>
    /// Huella de una imagen: 32×18 en escala de grises. Dos fotogramas del mismo
    /// lugar dan huellas cercanas aunque cambie un detalle. Público para tests.
    /// </summary>
    public static float[]? ImageSignature(string path)
    {
        using var bitmap = SkiaSharp.SKBitmap.Decode(path);
        if (bitmap is null) return null;
        using var small = bitmap.Resize(new SkiaSharp.SKImageInfo(32, 18), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear));
        if (small is null) return null;
        var sig = new float[32 * 18];
        for (var y = 0; y < 18; y++)
            for (var x = 0; x < 32; x++)
            {
                var c = small.GetPixel(x, y);
                sig[y * 32 + x] = 0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue;
            }
        return sig;
    }

    /// <summary>Diferencia media absoluta entre dos huellas (0 = iguales). Público para tests.</summary>
    public static float SignatureDistance(float[] a, float[] b)
    {
        var sum = 0f;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / a.Length;
    }

    // ── Imágenes ────────────────────────────────────────────────────────────

    private async Task<List<string>> DownloadKeyArtAsync(VideoResearch? research, string dir, CancellationToken ct)
    {
        var files = new List<string>();
        if (research is null) return files;
        foreach (var (url, name) in new[] { (research.BannerImageUrl, "keyart_banner"), (research.CoverImageUrl, "keyart_cover") })
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            var path = Path.Combine(dir, name + ".jpg");
            try
            {
                if (!File.Exists(path))
                    await File.WriteAllBytesAsync(path,
                        await httpFactory.CreateClient("youtube-research").GetByteArrayAsync(url, ct), ct);
                files.Add(path);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "No se pudo bajar el arte oficial {Url}", url);
            }
        }
        return files;
    }

    /// <summary>
    /// Pollinations: generación gratis y sin key, con dos condiciones verificadas
    /// el 24-nov-2026: UNA imagen cada 45 s por IP (si no, 402 FREE_LIMIT con
    /// "Retry after Ns") y una marca de agua abajo a la derecha que se recorta en
    /// <see cref="FrameArgs"/>. Por eso va secuencial, con pausa entre pedidos,
    /// y ante un 402 espera lo que dice el mensaje.
    /// </summary>
    private async Task<string?> GenerateImageAsync(VideoScene scene, int index, string dir, string n, CancellationToken ct)
    {
        var path = Path.Combine(dir, $"raw_{n}.jpg");
        if (File.Exists(path)) return path;
        if (string.IsNullOrWhiteSpace(scene.ImagePrompt)) return null;

        var prompt = $"{scene.ImagePrompt}, anime illustration style, cinematic lighting, highly detailed, no text";
        var url = $"https://image.pollinations.ai/prompt/{Uri.EscapeDataString(prompt)}" +
                  $"?width=1280&height=720&seed={1000 + index}";
        var http = httpFactory.CreateClient("pollinations");
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var wait = _nextPollinationsAt - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _nextPollinationsAt = DateTime.UtcNow + PollinationsInterval;
            try
            {
                using var resp = await http.GetAsync(url, ct);
                var type = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (resp.IsSuccessStatusCode && type.StartsWith("image/", StringComparison.Ordinal))
                {
                    await File.WriteAllBytesAsync(path, await resp.Content.ReadAsByteArrayAsync(ct), ct);
                    logger.LogInformation("Imagen IA escena {N} lista", n);
                    return path;
                }

                var body = await resp.Content.ReadAsStringAsync(ct);
                var retry = RetryAfter().Match(body);
                if (retry.Success)
                    _nextPollinationsAt = DateTime.UtcNow + TimeSpan.FromSeconds(int.Parse(retry.Groups[1].Value) + 2);
                logger.LogWarning("Pollinations {Code} (escena {N}, intento {Attempt}): {Body}",
                    (int)resp.StatusCode, n, attempt, body.Length > 200 ? body[..200] : body);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Pollinations falló (escena {N}, intento {Attempt}): {Msg}", n, attempt, ex.Message);
            }
        }
        return null;
    }

    private static readonly TimeSpan PollinationsInterval = TimeSpan.FromSeconds(47);
    private DateTime _nextPollinationsAt = DateTime.MinValue;

    [GeneratedRegex(@"Retry after (\d+)\s*s", RegexOptions.IgnoreCase)]
    private static partial Regex RetryAfter();

    /// <summary>
    /// Cualquier imagen → cuadro 1920x1080. Fondo: la misma imagen llenando el
    /// cuadro, desenfocada y oscurecida; encima, la imagen entera sin recortar.
    /// Una imagen 16:9 se tapa a sí misma; una portada vertical o un banner muy
    /// ancho quedan centrados sin deformarse. A las imágenes de IA se les corta
    /// el 7% de abajo (la marca de agua). Sin imagen: fondo liso de la marca.
    /// </summary>
    public static List<string> FrameArgs(string? image, string output, bool isAiImage)
    {
        if (image is null)
            return ["-y", "-f", "lavfi", "-i", "color=c=0x0b0b14:s=1920x1080", "-frames:v", "1", output];

        var crop = isAiImage ? "crop=iw:ih*0.93:0:0," : "";
        var filter =
            $"[0:v]{crop}split[a][b];" +
            "[a]scale=1920:1080:force_original_aspect_ratio=increase,crop=1920:1080,boxblur=28:4,eq=brightness=-0.18[bg];" +
            "[b]scale=1920:1080:force_original_aspect_ratio=decrease[fg];" +
            "[bg][fg]overlay=(W-w)/2:(H-h)/2,format=yuvj420p";
        return ["-y", "-i", image, "-filter_complex", filter, "-frames:v", "1", "-q:v", "2", output];
    }

    // ── Subtítulos ──────────────────────────────────────────────────────────

    /// <summary>
    /// Junta los SRT de cada escena en uno solo, corriendo cada uno por el
    /// inicio de su escena y renumerando los cues. Público para tests.
    /// </summary>
    public static string MergeSrt(IEnumerable<(string Srt, TimeSpan Offset)> parts)
    {
        var sb = new StringBuilder();
        var index = 1;
        foreach (var (srt, offset) in parts)
        {
            foreach (Match cue in SrtCue().Matches(srt.Replace("\r\n", "\n")))
            {
                var start = ParseSrtTime(cue.Groups["start"].Value) + offset;
                var end = ParseSrtTime(cue.Groups["end"].Value) + offset;
                sb.Append(index++).Append('\n')
                  .Append(FormatSrtTime(start)).Append(" --> ").Append(FormatSrtTime(end)).Append('\n')
                  .Append(cue.Groups["text"].Value.Trim()).Append("\n\n");
            }
        }
        return sb.ToString();
    }

    private static TimeSpan ParseSrtTime(string s) =>
        TimeSpan.ParseExact(s.Replace('.', ','), @"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);

    private static string FormatSrtTime(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";

    [GeneratedRegex(@"^\d+\n(?<start>\d\d:\d\d:\d\d[,.]\d{3}) --> (?<end>\d\d:\d\d:\d\d[,.]\d{3})\n(?<text>.*?)(?:\n\n|\n*\z)",
        RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex SrtCue();

    private static string Metadata(VideoPlan plan, TimeSpan duration) => new StringBuilder()
        .AppendLine($"TÍTULO: {plan.Title}")
        .AppendLine($"DURACIÓN: {duration:mm\\:ss}")
        .AppendLine().AppendLine("DESCRIPCIÓN:").AppendLine(plan.Description)
        .AppendLine().AppendLine("Fuentes:").AppendLine(string.Join("\n", plan.Sources.Select(s => "- " + s)))
        .AppendLine().AppendLine($"TAGS: {string.Join(", ", plan.Tags)}")
        .ToString();

    // ── Procesos ────────────────────────────────────────────────────────────

    /// <summary>
    /// Corre el backend de la voz clonada con un job de todas las escenas:
    ///   "kaggle" → Scripts/kaggle_tts.py (GPU T4 gratis, 30 h/semana; producción),
    ///   "space"  → Scripts/qwen_tts.py (Space de Hugging Face; da para pruebas cortas).
    /// Exit 3 = sin cuota de GPU, 4 = sin credenciales. Lo ya generado queda en
    /// caché y la próxima corrida sigue desde ahí.
    /// </summary>
    private async Task RunQwenTtsAsync(VideoPlan plan, VideoRenderOptions opt, string dir, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opt.CloneRefText))
            throw new InvalidOperationException("La voz clonada necesita la transcripción exacta de la referencia (--voice-ref-text)");

        var job = new
        {
            space = "Qwen/Qwen3-TTS",
            model = opt.QwenModel,
            @ref = Path.GetFullPath(opt.CloneRef!),
            refText = opt.CloneRefText,
            items = plan.Scenes.Select((s, i) => new
            {
                text = s.Narration,
                @out = Path.Combine(dir, $"scene_{(i + 1).ToString("00", CultureInfo.InvariantCulture)}.wav"),
            }),
        };

        // En GitHub Actions las voces las generan runners en paralelo
        // (youtube/tts_shard.py) y llegan ya hechas: no hay nada que llamar.
        if (job.items.All(it => File.Exists(it.@out)))
        {
            logger.LogInformation("Voz clonada: las {N} escenas ya están generadas", plan.Scenes.Count);
            return;
        }

        var jobPath = Path.Combine(dir, "qwen_job.json");
        await File.WriteAllTextAsync(jobPath, System.Text.Json.JsonSerializer.Serialize(job, VideoPlan.Json), ct);

        var kaggle = opt.QwenBackend.Equals("kaggle", StringComparison.OrdinalIgnoreCase);
        var script = Path.Combine(AppContext.BaseDirectory, "Scripts", kaggle ? "kaggle_tts.py" : "qwen_tts.py");
        var parts = opt.PythonCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var where = kaggle ? "Kaggle" : "Hugging Face";
        var sw = Stopwatch.StartNew();
        logger.LogInformation("Voz clonada: generando en {Where}…", where);
        try
        {
            // Kaggle encola, instala y baja el modelo: puede tardar bastante más que el render.
            var output = await RunAsync(parts[0], parts.Skip(1).Concat(["-I", script, jobPath]), dir, ct,
                TimeSpan.FromMinutes(kaggle ? 60 : 20));
            logger.LogInformation("Voz clonada lista en {Secs:F0}s: {Output}", sw.Elapsed.TotalSeconds, output.Trim());
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("exit 3", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Se acabó la cuota de GPU de {where}. Las escenas ya generadas quedaron guardadas: " +
                "volvé a correr el mismo comando más tarde y sigue desde ahí.", ex);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("exit 4", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Faltan las credenciales de Kaggle: cargá KAGGLE_API_TOKEN (kaggle.com → Settings → API → Generate New Token).", ex);
        }
    }

    /// <summary>
    /// Subtítulos aproximados para una voz sin tiempos: corta la narración en
    /// frases (y las frases largas en las comas) y reparte la duración según la
    /// cantidad de letras de cada pedazo. Público para tests.
    /// </summary>
    public static string EstimateSrt(string narration, double seconds)
    {
        var chunks = new List<string>();
        foreach (var sentence in SentenceSplit().Split(narration.Trim()).Where(s => s.Length > 0))
        {
            if (sentence.Length <= 90) { chunks.Add(sentence); continue; }
            var current = "";
            foreach (var piece in sentence.Split(", "))
            {
                var candidate = current.Length == 0 ? piece : current + ", " + piece;
                if (candidate.Length > 90 && current.Length > 0)
                {
                    chunks.Add(current + ",");
                    current = piece;
                }
                else current = candidate;
            }
            if (current.Length > 0) chunks.Add(current);
        }

        var total = chunks.Sum(c => c.Length);
        var sb = new StringBuilder();
        var t = 0.0;
        for (var i = 0; i < chunks.Count; i++)
        {
            var span = seconds * chunks[i].Length / total;
            sb.Append(i + 1).Append('\n')
              .Append(FormatSrtTime(TimeSpan.FromSeconds(t))).Append(" --> ")
              .Append(FormatSrtTime(TimeSpan.FromSeconds(t + span))).Append('\n')
              .Append(chunks[i]).Append("\n\n");
            t += span;
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"(?<=[.!?…])\s+")]
    private static partial Regex SentenceSplit();

    private Task RunTtsAsync(VideoRenderOptions opt, string text, string audio, string srt, CancellationToken ct)
    {
        // "edge-tts" en el runner; localmente puede ser "python -m edge_tts".
        var parts = opt.TtsCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var args = parts.Skip(1).Concat(
        [
            "--voice", opt.Voice, $"--rate={opt.Rate}", "--text", text,
            "--write-media", audio, "--write-subtitles", srt,
        ]).ToList();
        return RunAsync(parts[0], args, Path.GetDirectoryName(audio)!, ct);
    }

    private async Task<double> ProbeDurationAsync(VideoRenderOptions opt, string file, CancellationToken ct)
    {
        var output = await RunAsync(opt.FfprobePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file],
            Path.GetDirectoryName(file)!, ct);
        return double.Parse(output.Trim(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Corre un proceso con ArgumentList (sin armar una línea de comando a mano:
    /// la narración trae comillas y acentos). Devuelve stdout; si falla, lanza
    /// con el final de stderr.
    /// </summary>
    private static async Task<string> RunAsync(string file, IEnumerable<string> args, string workDir, CancellationToken ct, TimeSpan? limit = null)
        => (await RunFullAsync(file, args, workDir, ct, limit)).Out;

    /// <summary>Igual que <see cref="RunAsync"/> pero también devuelve stderr (donde ffmpeg escribe showinfo).</summary>
    private static async Task<(string Out, string Err)> RunFullAsync(string file, IEnumerable<string> args, string workDir, CancellationToken ct, TimeSpan? limit = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"No arrancó {file}");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit ?? TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new InvalidOperationException($"{file} no terminó en {(limit ?? TimeSpan.FromMinutes(10)).TotalMinutes:F0} minutos");
        }

        if (process.ExitCode != 0)
        {
            var tail = await stderr;
            if (tail.Length > 1500) tail = tail[^1500..];
            throw new InvalidOperationException($"{file} falló (exit {process.ExitCode}): {tail}");
        }
        return (await stdout, await stderr);
    }
}
