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
    bool RequireWordTimes = false,
    double ShotSeconds = 3.5,
    double MusicVolume = VideoMusic.DefaultVolume)
{
    public bool UsesClonedVoice => !string.IsNullOrWhiteSpace(CloneRef);
}

/// <summary>Una toma del video: qué escena acompaña, su lugar dentro de la escena y cuándo va.</summary>
public sealed record VideoShot(int Scene, int Slot, double Start, double Duration);

/// <summary>De dónde sale la imagen de una toma.</summary>
public enum ShotSource { SceneImage, TrailerClip, TrailerStill, Portrait }


/// <summary>
/// <see cref="VideoPlan"/> → video.mp4 horizontal 1080p, con el ritmo de los
/// Shorts de teorías que funcionan (analizado el 24-nov-2026 sobre uno de
/// "El Psy-Kongroo": voz continua, micropausas de 0,12–0,28 s solo en las comas,
/// imagen que cambia cada 1–1,5 s SIN esperar a la voz, 36% clips del anime):
///
///   VOZ: una sola pista continua. Cada escena se recorta en los bordes y se une
///   a la siguiente conservando un poco de su silencio natural. La voz no se
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
    MentionImageService mentions,
    ILogger<VideoRenderService> logger)
{
    private const int Fps = 30;

    /// <summary>Silencio entre escenas: el rango de las micropausas del Short de referencia.</summary>
    /// Desde el 26-nov-2026 es 0: la pausa la ponen los bordes naturales que deja
    /// <see cref="TrimVoiceArgs"/> (0,10 + 0,15 s con el aire de la voz). Rellenar con
    /// silencio digital hacía que ese aire se cortara y volviera en cada empalme (clicks).
    public const double SceneGapSeconds = 0;

    /// <summary>Colchón al final del video, después de la última frase.</summary>
    public const double EndTailSeconds = 1.0;

    /// <summary>
    /// Imagen limpia al final, sin voz ni subtítulos, para la pantalla final de
    /// YouTube (que pide entre 5 y 20 s en los últimos 20 s del video). Los
    /// elementos se cargan a mano en Studio: la API de YouTube no los expone.
    /// </summary>
    public const double EndScreenSeconds = 15.0;

    public async Task<string> RenderAsync(
        VideoPlan plan, VideoResearch? research, VideoRenderOptions opt, CancellationToken ct)
    {
        Directory.CreateDirectory(opt.OutputDir);
        var dir = Path.GetFullPath(opt.OutputDir);
        await File.WriteAllTextAsync(Path.Combine(dir, "plan.json"), plan.ToJson(), ct);

        var keyArt = await DownloadKeyArtAsync(research, dir, ct);
        var subtitles = new List<(string Srt, TimeSpan Offset)>();
        var keyArtTurn = 0;

        // El metraje (tráiler de AniList o las fuentes del plan) se baja y se corta
        // en paralelo con la voz.
        var footageJob = PrepareFootageAsync(plan, research, opt, dir, ct);

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
        // Con tomas ancladas cada imagen IA la pide su toma ("ai: ..."): no se
        // genera una por escena.
        var anchored = plan.Scenes.Any(s => s.Shots is { Count: > 0 });
        var imageJob = Task.Run(async () =>
        {
            for (var i = 0; i < plan.Scenes.Count; i++)
            {
                try
                {
                    var wantsAi = !anchored && !UsesKeyArt(plan.Scenes[i]) && aiBudget > 0;
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
            // La voz lee el texto con el glosario de pronunciación aplicado; los
            // subtítulos siempre muestran la narración original.
            var speech = VideoPlan.ForSpeech(scene.Narration, plan.Pronunciations);
            if (!opt.UsesClonedVoice)
                await RunTtsAsync(opt, speech, rawAudio, srt, ct);

            // Sin el silencio que deja cada generación en los bordes: es lo que
            // hacía sonar "cortado" el cambio de escena.
            var voice = Path.Combine(dir, $"voice_{n}.wav");
            await RunAsync(opt.FfmpegPath, TrimVoiceArgs(rawAudio, voice), dir, ct);
            var seconds = await ProbeDurationAsync(opt, voice, ct);
            // Qwen no da tiempos; con Edge, si el glosario cambió el texto, su SRT
            // mostraría "Yuyutsu": en los dos casos se estima sobre la narración original.
            if (opt.UsesClonedVoice || speech != scene.Narration)
                await File.WriteAllTextAsync(srt, EstimateSrt(scene.Narration, seconds), ct);

            var gap = i == plan.Scenes.Count - 1 ? EndTailSeconds : SceneGapSeconds;
            subtitles.Add((await File.ReadAllTextAsync(srt, ct), TimeSpan.FromSeconds(elapsed)));
            voiceParts.Add(voice);
            gaps.Add(gap);
            spans.Add(seconds + gap);
            elapsed += seconds + gap;
        }
        // La pantalla final va después de la última escena: la voz se estira con
        // silencio, pero no entra en spans (sus tomas son aparte, ver más abajo).
        var spokenEnd = elapsed;
        gaps[^1] += EndScreenSeconds;
        elapsed += EndScreenSeconds;
        // Unida en código, muestra por muestra (ver AudioMix): el concat de ffmpeg
        // no es el lugar donde pueda perderse una palabra.
        var voiceTrack = AudioMix.Join(voiceParts.Select(PcmAudio.Read).ToList(), gaps);
        voiceTrack.Write(Path.Combine(dir, "voice.wav"));
        if (Math.Abs(voiceTrack.Seconds - elapsed) > 0.05)
            throw new InvalidOperationException($"La voz unida dura {voiceTrack.Seconds:F2}s y debería durar {elapsed:F2}s");
        logger.LogInformation("Voz continua: {Scenes} escenas, {Secs:F1}s + {End:F0}s de pantalla final",
            plan.Scenes.Count, spokenEnd, EndScreenSeconds);

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
        var footage = await footageJob;

        // ── 3. Tomas ───────────────────────────────────────────────────────
        // Con "shots" en el plan, cada toma arranca cuando la voz DICE sus
        // palabras; sin ellos (planes viejos), tomas de duración fija.
        var shots = anchored
            ? await PlanAnchoredAsync(plan, voiceParts, spans, opt, dir, ct)
            : PlanShots(spans, opt.ShotSeconds);
        var shotFiles = new List<string>();
        // De qué fuentes sale el metraje de cada escena, y qué ya se presentó: lo
        // que se vuelve a nombrar va con metraje (ver director.md).
        var routes = FootageRouter.Route(plan, FootageSources(plan, footage));
        var intro = IntroductionLedger.Apply(plan);
        var director = DirectorReport(plan, routes, intro);
        string? lastPortrait = null;
        var counts = new Dictionary<ShotSource, int>();
        var matched = 0;
        var aiLeft = new[] { MaxAiImages }; // imágenes IA que piden las tomas "ai: ..."
        // Qué fotograma de los videos oficiales muestra cada descripción del storyboard.
        // Y en qué planos aparece cada personaje que se vuelve a nombrar.
        var faces = footage.IsEmpty ? [] : await CastFacesAsync(plan, intro, dir, ct);
        var matches = footage.IsEmpty ? [] : await MatchFramesAsync(plan, footage, faces, opt, dir, ct);
        for (var k = 0; k < shots.Count; k++)
        {
            var shot = shots[k];
            var startFrame = (int)Math.Round(shot.Start * Fps);
            var frameCount = Math.Max(1, (int)Math.Round((shot.Start + shot.Duration) * Fps) - startFrame);
            var flash = k > 0 && k % 4 == 0; // flash blanco cada ~4 cortes, como la referencia
            var segments = footage.Stills.Count;
            var source = ChooseShotSource(shot, k, segments, segments);
            var group = routes[shot.Scene].Ids;

            // Lo que dice la toma manda. Si nombra a un personaje: su retrato
            // oficial. Si describe una imagen: el plano de los videos oficiales que
            // CLIP encontró más parecido (como clip si dura lo suficiente), de la
            // fuente de lo que se está contando.
            string? portrait = null;
            FootagePick? pick = null;
            var resolved = false;
            var gentle = false;
            string? query;
            string? seen = null; // personaje nombrado otra vez que aparece en el plano elegido
            var repeated = false; // su imagen otra vez: no hubo plano nuevo donde aparezca
            if (anchored)
            {
                // Lo ya presentado y el relleno (Slot -1) van con metraje.
                var spec = shot.Slot >= 0 && shot.Slot < intro.Specs[shot.Scene].Count ? intro.Specs[shot.Scene][shot.Slot] : null;
                source = k % 2 == 0 ? ShotSource.TrailerClip : ShotSource.TrailerStill;
                if (footage.IsEmpty) source = ShotSource.SceneImage;
                query = spec is { Kind: ShotKind.Scene, Value.Length: > 0 } ? spec.Value : null;
                if (spec is { Again: true })
                {
                    // Ya presentado: un plano NUEVO donde aparezca (de cualquier fuente,
                    // no solo la de la escena); si no hay, su imagen otra vez, con
                    // movimiento. Nunca un plano cualquiera.
                    if (spec.Kind == ShotKind.Character)
                        foreach (var name in spec.Names)
                            if (matches.TryGetValue(FaceKey(name), out var ranked)
                                && footage.PickMatched(ranked, [], MinCastMargin, shot.Duration) is { } found)
                            {
                                pick = found;
                                source = found.AsClip ? ShotSource.TrailerClip : ShotSource.TrailerStill;
                                resolved = true;
                                seen = name;
                                break;
                            }
                    if (!resolved && await MentionFrameAsync(spec with { Again = false }, keyArt, dir, opt, aiLeft, ct) is { } again)
                    {
                        portrait = again;
                        source = ShotSource.Portrait;
                        resolved = true;
                        lastPortrait = again;
                        repeated = true; // con paneo (gentle = false): no se ve igual que la primera vez
                    }
                    if (!resolved) logger.LogWarning("Toma {K}: sin plano ni imagen para \"{Show}\" — va un clip", k + 1, IntroductionLedger.Describe(spec));
                }
                else if (spec is { Kind: not ShotKind.Scene })
                {
                    portrait = await MentionFrameAsync(spec, keyArt, dir, opt, aiLeft, ct);
                    if (portrait is not null)
                    {
                        source = ShotSource.Portrait;
                        resolved = true;
                        gentle = true; // placas, logos, pósters y retratos: sin paneos que corten el texto
                        lastPortrait = portrait;
                    }
                    else logger.LogWarning("Toma {K}: sin imagen para \"{Show}\" — va un clip", k + 1, IntroductionLedger.Describe(spec));
                }
            }
            else
            {
                var queries = plan.Scenes[shot.Scene].VisualQueries;
                query = queries is { Count: > 0 } ? queries[shot.Slot % queries.Count] : null;
            }
            if (!resolved && query is not null)
            {
                portrait = anchored ? null : await PickPortraitAsync(query, lastPortrait, dir, opt, ct);
                if (portrait is not null)
                {
                    source = ShotSource.Portrait;
                    lastPortrait = portrait;
                    resolved = true;
                }
                else if (matches.TryGetValue(query, out var ranked)
                         && footage.PickMatched(ranked, group, MinFrameMatch, shot.Duration) is { } best)
                {
                    pick = best;
                    source = best.AsClip ? ShotSource.TrailerClip : ShotSource.TrailerStill;
                    resolved = true;
                    matched++;
                }
            }

            // Sin coincidencia: el siguiente plano sin usar de la fuente de la
            // escena. Un clip sirve si cubre al menos el 60% de la toma (el resto con
            // cámara lenta); si no, su fotograma. Un plano usado como clip tampoco
            // vuelve como fotograma (ni desde otra fuente que lo repita).
            if (!resolved && source is ShotSource.TrailerClip or ShotSource.TrailerStill)
            {
                pick = footage.PickFiller(group, source == ShotSource.TrailerClip, shot.Duration);
                source = pick is null ? ShotSource.SceneImage
                    : pick.AsClip ? ShotSource.TrailerClip : ShotSource.TrailerStill;
            }
            counts[source] = counts.GetValueOrDefault(source) + 1;
            director.Append(CultureInfo.InvariantCulture, $"{k + 1,3} {shot.Start,7:F2}s  esc {shot.Scene + 1,2}  ");
            director.AppendLine(source switch
            {
                ShotSource.TrailerClip or ShotSource.TrailerStill =>
                    $"{pick!.Pool.Id} @{pick.Segment.Time:F1}s {(pick.AsClip ? "clip" : "fotograma")}" +
                    (seen is not null ? $" ← aparece «{seen}»" : query is not null && resolved ? $" ← CLIP «{query}»" : ""),
                ShotSource.Portrait => Path.GetFileName(portrait) + (repeated ? " (otra vez: sin plano nuevo donde aparezca)" : ""),
                _ => "imagen de la escena",
            });

            var output = Path.Combine(dir, $"shot_{(k + 1).ToString("000", CultureInfo.InvariantCulture)}.mp4");
            var args = source switch
            {
                ShotSource.TrailerClip => ClipShotArgs(pick!.Pool.Path, pick.Segment.Start, frameCount, flash, output,
                    slowdown: Math.Max(1, shot.Duration / pick.Segment.Length)),
                ShotSource.TrailerStill => ImageShotArgs(pick!.Segment.Still, frameCount, k, flash, output),
                ShotSource.Portrait => ImageShotArgs(portrait!, frameCount, gentle ? 0 : k, flash, output, gentle),
                _ => ImageShotArgs(frames[shot.Scene], frameCount, k, flash, output),
            };
            await RunAsync(opt.FfmpegPath, args, dir, ct);
            shotFiles.Add(output);
        }
        logger.LogInformation("Tomas: {Total} ({Mix})", shots.Count,
            string.Join(", ", counts.Select(c => $"{c.Key}={c.Value}")));
        await File.WriteAllTextAsync(Path.Combine(dir, "director.md"), director.Append("```\n").ToString(), ct);
        logger.LogInformation("Dirección: {Changes} menciones ya presentadas pasan a metraje — ver director.md",
            intro.Changes.Count);

        // Pantalla final: la imagen de portada quieta, desenfocada y oscurecida,
        // así los elementos de YouTube se leen encima. Sin voz ni subtítulos.
        var endScreen = Path.Combine(dir, "shot_end.mp4");
        var endFrames = (int)Math.Round(elapsed * Fps) - (int)Math.Round(spokenEnd * Fps);
        await RunAsync(opt.FfmpegPath, EndScreenArgs(frames[0], endFrames, endScreen), dir, ct);
        shotFiles.Add(endScreen);

        // ── 4. Unir tomas + voz + subtítulos ───────────────────────────────
        await File.WriteAllLinesAsync(Path.Combine(dir, "shots.txt"),
            shotFiles.Select(s => $"file '{Path.GetFileName(s)}'"), ct);
        await RunAsync(opt.FfmpegPath,
            ["-y", "-f", "concat", "-safe", "0", "-i", "shots.txt", "-c", "copy", "visuals.mp4"], dir, ct);

        await File.WriteAllTextAsync(Path.Combine(dir, "subs.srt"), MergeSrt(subtitles), new UTF8Encoding(false), ct);
        var final = Path.Combine(dir, "video.mp4");
        var (musicBed, credits) = await PrepareMusicAsync(plan, opt, dir, ct);
        var audio = await MixAudioAsync(musicBed, opt, elapsed, dir, ct);
        await RunAsync(opt.FfmpegPath, FinalMuxArgs(audio, elapsed), dir, ct);
        await CheckAudioGapsAsync(opt, final, dir, ct);

        var duration = TimeSpan.FromSeconds(elapsed);
        await File.WriteAllTextAsync(Path.Combine(dir, "youtube.txt"), Metadata(plan, duration, credits), ct);
        await RenderThumbnailsAsync(plan, keyArt, dir, ct);
        logger.LogInformation("Video listo: {Path} ({Duration:mm\\:ss})", final, duration);
        return final;
    }

    /// <summary>
    /// Baja 3 tracks del mood del plan y los une en music.wav (en el mux final va
    /// en loop si el video dura más). Devuelve null si está apagada o falla la
    /// descarga: el video sale igual, sin música. Los MP3 quedan en caché.
    /// </summary>
    private async Task<(string? Bed, IReadOnlyList<string> Credits)> PrepareMusicAsync(
        VideoPlan plan, VideoRenderOptions opt, string dir, CancellationToken ct)
    {
        if (opt.MusicVolume <= 0) return (null, []);
        var tracks = VideoMusic.PickTracks(plan.Mood, plan.Title, count: 3);
        try
        {
            var http = httpFactory.CreateClient("youtube-research");
            var files = new List<(string Path, int StartSeconds)>();
            foreach (var t in tracks)
            {
                var file = Path.Combine(dir, $"music_{Regex.Replace(t.Title, @"[^\w]+", "_")}.mp3");
                if (!File.Exists(file))
                    await File.WriteAllBytesAsync(file, await http.GetByteArrayAsync(t.Url, ct), ct);
                files.Add((Path.GetFileName(file), t.StartSeconds));
            }
            await RunAsync(opt.FfmpegPath, VideoMusic.MusicBedArgs(files, "music.wav"), dir, ct);
            logger.LogInformation("Música ({Mood}): {Tracks}", plan.Mood ?? "dark",
                string.Join(" → ", tracks.Select(t => t.Title)));
            return ("music.wav", tracks.Select(t => t.Attribution).OfType<string>().ToList());
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Sin música de fondo: {Msg}", ex.Message);
            return (null, []);
        }
    }

    /// <summary>Solo las miniaturas (modo --thumbnail-only: para iterar el diseño sin renderizar el video).</summary>
    public async Task RenderThumbnailsAsync(VideoPlan plan, VideoResearch? research, string outputDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var dir = Path.GetFullPath(outputDir);
        await RenderThumbnailsAsync(plan, await DownloadKeyArtAsync(research, dir, ct), dir, ct);
    }

    /// <summary>
    /// Las variantes de miniatura (<see cref="ThumbnailPlanner"/>): fondo de arte
    /// oficial, personajes recortados con <see cref="AnimeCutout"/> y el gancho.
    /// Deja thumbnail_report.txt con los chequeos. Si algo falla, queda una miniatura
    /// sin personajes en vez de cortar el render.
    /// </summary>
    private async Task RenderThumbnailsAsync(VideoPlan plan, IReadOnlyList<string> keyArt, string dir, CancellationToken ct)
    {
        var variants = ThumbnailPlanner.Variants(plan);
        var report = new StringBuilder();
        using var background = keyArt.Select(p => SkiaSharp.SKBitmap.Decode(p)).FirstOrDefault(b => b is not null);
        var subjects = new List<ThumbnailSubject>();
        try
        {
            var http = httpFactory.CreateClient("youtube-research");
            AnimeCutout? cutout = null;
            try
            {
                cutout = await AnimeCutout.LoadAsync(http, null, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException
                                           or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Recorte de personajes no disponible ({Msg}): retratos enmarcados", ex.Message);
            }
            using (cutout)
            {
                foreach (var name in variants[0].Characters)
                {
                    // Primero AniList (retrato oficial); si no está (temporadas nuevas), la wiki,
                    // como las tomas "character:" del video.
                    string? raw = null;
                    if (visuals.FindCharacter(name) is { } candidate)
                    {
                        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(candidate.Url)))[..12];
                        raw = Path.Combine(dir, $"portrait_{hash}.img");
                        if (!File.Exists(raw))
                            await File.WriteAllBytesAsync(raw, await http.GetByteArrayAsync(candidate.Url, ct), ct);
                    }
                    raw ??= await mentions.CharacterAsync(name, dir, ct);
                    if (raw is null) { report.AppendLine($"personaje sin imagen: {name}"); continue; }
                    var bitmap = SkiaSharp.SKBitmap.Decode(raw);
                    if (bitmap is null) continue;
                    if (cutout is not null)
                    {
                        var (cut, coverage) = cutout.Cut(bitmap);
                        report.AppendLine($"{name}: recorte cubre {coverage:P0} de la imagen");
                        // Muy poco = no encontró al personaje; casi todo = no separó el fondo.
                        if (coverage is > 0.08 and < 0.92) { bitmap.Dispose(); subjects.Add(new(cut, true)); continue; }
                        cut.Dispose();
                    }
                    subjects.Add(new(bitmap, false));
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Personajes para la miniatura no disponibles: {Msg}", ex.Message);
        }

        try
        {
            foreach (var v in variants)
            {
                var result = ThumbnailRenderer.Render(new ThumbnailSpec(background, subjects, v.Text), Path.Combine(dir, v.File));
                // Si el texto no se lee en el feed, se prueba con las dos primeras palabras.
                if (!result.Ok && v.Text is { } text && text.Split(' ').Length > 2)
                    result = ThumbnailRenderer.Render(new ThumbnailSpec(background, subjects,
                        string.Join(' ', text.Split(' ').Take(2))), Path.Combine(dir, v.File));
                report.AppendLine($"{v.File}: {string.Join(" + ", v.Characters.DefaultIfEmpty("sin personajes"))} · " +
                    $"«{v.Text ?? "sin texto"}» · letra {result.FontSize:F0}px, mayúscula en el feed {result.FeedCapHeight:F1}px" +
                    (result.Ok ? "" : " · " + string.Join("; ", result.Warnings)));
            }
        }
        finally
        {
            foreach (var s in subjects) s.Image.Dispose();
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "thumbnail_report.txt"), report.ToString(), ct);
        logger.LogInformation("Miniaturas: {Files}\n{Report}", string.Join(", ", variants.Select(v => v.File)), report.ToString().TrimEnd());
    }

    /// <summary>
    /// Audio final en WAV: voz (48 kHz estéreo) + música con ducking, mezclados
    /// en código (<see cref="AudioMix"/>), y llevado a −14 LUFS con loudnorm en dos
    /// pasadas. ffmpeg solo hace pasos de una entrada (remuestrear, recortar la
    /// música, loudnorm); después de cada uno se verifica el largo.
    /// </summary>
    private async Task<string> MixAudioAsync(string? musicBed, VideoRenderOptions opt, double duration, string dir, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var frames = (int)Math.Round(duration * 48000);
        await RunAsync(opt.FfmpegPath, ["-y", "-i", "voice.wav", "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le", "voice48.wav"], dir, ct);
        var voice = PcmAudio.Read(Path.Combine(dir, "voice48.wav"));
        if (Math.Abs(voice.Frames - frames) > 4800)
            throw new InvalidOperationException($"La voz a 48 kHz dura {voice.Seconds:F2}s y debería durar {duration:F2}s");
        voice = AudioMix.Fit(voice, frames);

        PcmAudio? music = null;
        if (musicBed is not null && opt.MusicVolume > 0)
        {
            await RunAsync(opt.FfmpegPath, ["-y", "-stream_loop", "-1", "-i", musicBed, "-t", duration.ToString("0.###", inv),
                "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le", "music_fit.wav"], dir, ct);
            music = AudioMix.Fit(PcmAudio.Read(Path.Combine(dir, "music_fit.wav")), frames);
        }
        AudioMix.Mix(voice, music, opt.MusicVolume).Write(Path.Combine(dir, "mix.wav"));

        var (_, err) = await RunFullAsync(opt.FfmpegPath, AudioMix.LoudnessMeasureArgs("mix.wav"), dir, ct);
        var measured = AudioMix.ParseLoudness(err);
        await RunAsync(opt.FfmpegPath, AudioMix.LoudnessApplyArgs("mix.wav", "mix_norm.wav", measured), dir, ct);
        var normalized = PcmAudio.Read(Path.Combine(dir, "mix_norm.wav"));
        if (Math.Abs(normalized.Frames - frames) > 4800)
            throw new InvalidOperationException($"El audio normalizado dura {normalized.Seconds:F2}s y debería durar {duration:F2}s");
        logger.LogInformation("Audio: {In} LUFS → {Target} LUFS (pico {Tp} dBTP), {Secs:F1}s sin huecos",
            measured.GetValueOrDefault("input_i"), AudioMix.TargetLufs, measured.GetValueOrDefault("input_tp"), normalized.Seconds);
        return "mix_norm.wav";
    }

    /// <summary>
    /// El AAC del video no puede tener huecos de timestamps: cada hueco es voz
    /// que no suena. Si aparece alguno, el render falla en vez de entregar un
    /// video con palabras comidas.
    /// </summary>
    private async Task CheckAudioGapsAsync(VideoRenderOptions opt, string video, string dir, CancellationToken ct)
    {
        var csv = await RunAsync(opt.FfprobePath,
            ["-v", "error", "-select_streams", "a", "-show_entries", "packet=pts_time", "-of", "csv=p=0", video], dir, ct);
        var gaps = AudioMix.PtsGaps(AudioMix.ParsePts(csv));
        if (gaps.Count > 0)
            throw new InvalidOperationException(
                $"El audio del video tiene {gaps.Count} huecos ({gaps.Sum(g => g.To - g.From):F1}s): " +
                string.Join(", ", gaps.Take(5).Select(g => $"{g.From:F2}→{g.To:F2}")));
        logger.LogInformation("Audio del video verificado: sin huecos");
    }

    /// <summary>
    /// Mux final: tomas + subtítulos quemados + el audio ya mezclado (sin filtros
    /// de audio: solo se codifica). Rutas relativas a propósito: en Windows "C:"
    /// rompe el filtro de subtítulos. Público para tests.
    /// </summary>
    public static List<string> FinalMuxArgs(string audio, double duration) =>
    [
        "-y", "-i", "visuals.mp4", "-i", audio,
        "-filter_complex", "[0:v]subtitles=subs.srt:force_style='FontName=Arial,FontSize=15,Bold=1,Outline=2,Shadow=0,MarginV=28'[vout]",
        "-map", "[vout]", "-map", "1:a",
        "-t", duration.ToString("0.###", CultureInfo.InvariantCulture),
        "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "video.mp4",
    ];

    /// <summary>Imágenes IA como máximo por video (Pollinations: 1 cada 45 s).</summary>
    public const int MaxAiImages = 10;

    /// <summary>Similitud CLIP mínima para aceptar un fotograma como "lo que se dice".</summary>
    public const float MinFrameMatch = 0.27f;

    /// <summary>
    /// Un plano muestra a un personaje si se parece a su retrato más que a cualquier
    /// otro retrato del elenco (margen &gt; 0; ver Scripts/frame_match.py). Medido el
    /// 27-nov-2026 con el tráiler de la T1 de Edgerunners contra el elenco de AniList
    /// (25 retratos): 3 de 3 planos aceptados correctos, pero encuentra pocos (3 de
    /// 19 planos claros de David y Lucy). Con un elenco de 4 la precisión caía a
    /// 0,3–0,5: por eso se compara contra el elenco entero de la serie.
    /// </summary>
    public const float MinCastMargin = 0f;

    private static string FaceKey(string name) => "face:" + name;

    /// <summary>
    /// Retratos de referencia para buscar en el metraje a los personajes que se
    /// vuelven a nombrar: el elenco de AniList de cada serie con póster en el plan,
    /// más el retrato de cada nombrado que no esté en ese elenco. Clave: el nombre
    /// tal como lo escribe el plan para los nombrados; el de AniList para el resto.
    /// </summary>
    private async Task<Dictionary<string, string>> CastFacesAsync(VideoPlan plan, IntroductionResult intro, string dir, CancellationToken ct)
    {
        var faces = new Dictionary<string, string>(StringComparer.Ordinal);
        var wanted = intro.Specs.SelectMany(s => s).OfType<ShotReference>()
            .Where(r => r is { Again: true, Kind: ShotKind.Character }).SelectMany(r => r.Names).Distinct().ToList();
        if (wanted.Count == 0) return faces;

        var series = plan.Scenes.SelectMany(s => s.Shots ?? []).Select(s => ShotReference.Parse(s.Show))
            .Where(r => r.Kind == ShotKind.Poster && r.Value.Length > 0).Select(r => r.Value)
            .DistinctBy(v => string.Join(' ', ShotTiming.Tokens(v))).Take(3).ToList();
        var cast = new List<(string Name, string Path)>();
        foreach (var s in series)
            foreach (var c in await mentions.SeriesCastAsync(s, dir, ct))
                if (!cast.Any(x => x.Path == c.Path)) cast.Add(c);

        foreach (var name in wanted)
        {
            var match = cast.FirstOrDefault(c => SameCharacter(name, c.Name));
            var path = match.Path ?? await mentions.CharacterAsync(name, dir, ct);
            if (path is not null) faces[name] = path;
        }
        foreach (var (name, path) in cast)
            if (!faces.ContainsValue(path)) faces.TryAdd(name, path);
        logger.LogInformation("Personajes nombrados otra vez: {Wanted} — {Found} con retrato, contra un elenco de {Cast}",
            wanted.Count, wanted.Count(faces.ContainsKey), faces.Count);
        return faces;
    }

    /// <summary>
    /// "David Martínez" = "David Martinez"; "Lucy Kushinada" = "Lucy" (AniList a veces
    /// guarda solo el nombre de pila): el mismo primer nombre y uno contiene al otro.
    /// Público para tests.
    /// </summary>
    public static bool SameCharacter(string a, string b)
    {
        var x = ShotTiming.Tokens(MentionImageService.WithoutAccents(a));
        var y = ShotTiming.Tokens(MentionImageService.WithoutAccents(b));
        if (x.Count == 0 || y.Count == 0 || x[0] != y[0]) return false;
        return x.All(y.Contains) || y.All(x.Contains);
    }

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
        VideoPlan plan, FootageLibrary footage, IReadOnlyDictionary<string, string> faces,
        VideoRenderOptions opt, string dir, CancellationToken ct)
    {
        var queries = plan.Scenes.SelectMany(s => s.VisualQueries ?? [])
            .Where(q => visuals.FindCharacter(q) is null)
            .Concat(plan.Scenes.SelectMany(s => s.Shots ?? [])
                .Select(s => ShotReference.Parse(s.Show)).Where(r => r.Kind == ShotKind.Scene && r.Value.Length > 0).Select(r => r.Value))
            .Distinct().ToList();
        var result = new Dictionary<string, List<(int, float)>>();
        if (queries.Count == 0 && faces.Count == 0) return result;

        var job = Path.Combine(dir, "frame_match_job.json");
        var output = Path.Combine(dir, "frame_match.json");
        // Todos los puntajes (top = todos los fotogramas): después cada toma se queda
        // con los de la fuente de su escena.
        await File.WriteAllTextAsync(job, System.Text.Json.JsonSerializer.Serialize(
            new { frames = footage.Stills.Select(s => s.Segment.Still), queries, top = footage.Stills.Count, faces }), ct);
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
                queries.Count, footage.Stills.Count);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or System.Text.Json.JsonException)
        {
            logger.LogWarning("CLIP no disponible ({Msg}) — los clips van en rotación",
                ex.Message.Length > 300 ? ex.Message[..300] : ex.Message);
        }
        return result;
    }

    // ── Tomas ancladas a la narración ──────────────────────────────────────

    /// <summary>
    /// Tiempos reales de cada palabra (alineación forzada) → en qué segundo arranca
    /// cada toma del storyboard. Deja shots_plan.txt con cada corte (escena,
    /// segundo, palabras, qué se muestra) para revisar la sincronía sin ver el video.
    /// </summary>
    private async Task<List<VideoShot>> PlanAnchoredAsync(VideoPlan plan, IReadOnlyList<string> voiceParts,
        IReadOnlyList<double> spans, VideoRenderOptions opt, string dir, CancellationToken ct)
    {
        // Lo que la voz LEYÓ (con el glosario aplicado), no la narración: es lo que suena.
        var spoken = plan.Scenes.Select(s => VideoPlan.ForSpeech(s.Narration, plan.Pronunciations)).ToList();
        var heard = await WordTimesAsync(voiceParts, spoken, opt, dir, ct);
        var anchors = new List<IReadOnlyList<double?>>();
        var report = new StringBuilder();
        var sceneStart = 0.0;
        var missing = 0;
        for (var i = 0; i < plan.Scenes.Count; i++)
        {
            var scene = plan.Scenes[i];
            var seconds = spans[i] - (i == plan.Scenes.Count - 1 ? EndTailSeconds : SceneGapSeconds);
            var starts = ShotTiming.WordStarts(scene.Narration, heard.GetValueOrDefault(voiceParts[i]) ?? [], seconds);
            var list = new List<double?>();
            var from = 0;
            foreach (var spec in scene.Shots ?? [])
            {
                var found = ShotTiming.FindAnchor(scene.Narration, starts, spec.At, from);
                if (found is { } f) from = f.Word;
                else if (list.Count > 0) missing++;
                list.Add(found?.Time);
                report.Append(CultureInfo.InvariantCulture,
                    $"{i + 1,2} {sceneStart + (found?.Time ?? 0),7:F2}s  «{spec.At}»{(found is null && list.Count > 1 ? " (NO ENCONTRADO)" : "")} → {spec.Show}\n");
            }
            anchors.Add(list);
            sceneStart += spans[i];
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "shots_plan.txt"), report.ToString(), ct);
        var shots = ShotTiming.PlanAnchoredShots(spans, anchors, opt.ShotSeconds);
        logger.LogInformation("Tomas ancladas: {Shots} ({Named} con referencia, {Missing} anclas no encontradas, tiempos {Src})",
            shots.Count, shots.Count(s => s.Slot >= 0), missing, heard.Count > 0 ? "alineados con la voz" : "estimados por letras");
        return shots;
    }

    /// <summary>
    /// Corre Scripts/word_times.py: alineación forzada del texto que leyó la voz
    /// (<paramref name="spoken"/>) contra el audio de cada escena (ya recortado, el
    /// que suena en el video). Si falla, devuelve vacío y los tiempos se estiman por
    /// letras — salvo con <see cref="VideoRenderOptions.RequireWordTimes"/>, que corta el render.
    /// </summary>
    private async Task<Dictionary<string, List<(string Word, double Start)>>> WordTimesAsync(
        IReadOnlyList<string> voiceParts, IReadOnlyList<string> spoken, VideoRenderOptions opt, string dir, CancellationToken ct)
    {
        var result = new Dictionary<string, List<(string, double)>>();
        var job = Path.Combine(dir, "word_times_job.json");
        var output = Path.Combine(dir, "word_times.json");
        await File.WriteAllTextAsync(job, System.Text.Json.JsonSerializer.Serialize(
            new { files = voiceParts, texts = spoken }), ct);
        try
        {
            var parts = opt.MatcherPython.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var script = Path.Combine(AppContext.BaseDirectory, "Scripts", "word_times.py");
            var sw = Stopwatch.StartNew();
            var (_, err) = await RunFullAsync(parts[0], parts.Skip(1).Concat(["-I", script, job, output]), dir, ct, TimeSpan.FromMinutes(30));
            // Por escena: palabras alineadas y confianza (marca las dudosas).
            foreach (var line in err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (line.StartsWith("voice_", StringComparison.Ordinal) || line.StartsWith("word_times:", StringComparison.Ordinal))
                    logger.LogInformation("Alineado {Line}", line);
            using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(output, ct));
            foreach (var file in doc.RootElement.EnumerateObject())
                result[file.Name] = file.Value.EnumerateArray()
                    .Select(w => (w[0].GetString() ?? "", w[1].GetDouble())).ToList();
            logger.LogInformation("Alineación forzada: tiempos de {Words} palabras en {Secs:F0}s",
                result.Values.Sum(v => v.Count), sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or System.Text.Json.JsonException)
        {
            var msg = ex.Message.Length > 600 ? ex.Message[^600..] : ex.Message;
            if (opt.RequireWordTimes)
                throw new InvalidOperationException($"Sin tiempos por palabra (--require-word-times): {msg}", ex);
            logger.LogWarning("Alineación no disponible ({Msg}) — tiempos estimados por letras", msg);
        }
        return result;
    }

    /// <summary>
    /// Referencia de una toma (póster, logo, tarjeta, personaje, IA, arte) →
    /// cuadro 1920x1080 listo para la toma. Null si no hay imagen: la toma va
    /// con un clip. Los cuadros quedan en caché en mentions/.
    /// </summary>
    private async Task<string?> MentionFrameAsync(ShotReference r, IReadOnlyList<string> keyArt, string dir,
        VideoRenderOptions opt, int[] aiLeft, CancellationToken ct)
    {
        var key = $"{r.Kind}|{r.Value}|{r.Detail}|{r.Image}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..12];
        Directory.CreateDirectory(Path.Combine(dir, "mentions"));
        var frame = Path.Combine(dir, "mentions", $"frame_{r.Kind.ToString().ToLowerInvariant()}_{hash}.jpg");
        if (File.Exists(frame)) return frame;
        var background = keyArt.FirstOrDefault();
        try
        {
            switch (r.Kind)
            {
                case ShotKind.Logo:
                    var logos = new List<(string, string?)>();
                    foreach (var name in r.Names) logos.Add((name, await mentions.LogoAsync(name, dir, ct)));
                    if (logos.Count == 0) return null;
                    CardRenderer.RenderLogos(logos, background, frame);
                    return frame;
                case ShotKind.Card:
                    if (r.Value.Length == 0) return null;
                    var image = r.Image is null ? null : await RawMentionImageAsync(ShotReference.Parse(r.Image), keyArt, dir, ct);
                    CardRenderer.RenderCard(r.Value, r.Detail, image, image ?? background, frame);
                    return frame;
                case ShotKind.Character or ShotKind.Photo:
                    var people = new List<(string, string)>();
                    foreach (var name in r.Names)
                        if (await RawMentionImageAsync(r with { Value = name }, keyArt, dir, ct) is { } pic) people.Add((name, pic));
                    if (people.Count == 0) return null;
                    CardRenderer.RenderPortraits(people, background, frame);
                    return frame;
                case ShotKind.Ai:
                    if (aiLeft[0] <= 0 || r.Value.Length == 0) return null;
                    aiLeft[0]--;
                    var raw = await GenerateImageAsync(new VideoScene { ImagePrompt = r.Value },
                        BitConverter.ToUInt16(Convert.FromHexString(hash[..4])), Path.Combine(dir, "mentions"), "ai_" + hash, ct);
                    if (raw is null) return null;
                    await RunAsync(opt.FfmpegPath, FrameArgs(raw, frame, isAiImage: true), dir, ct);
                    return frame;
                default:
                    var picture = await RawMentionImageAsync(r, keyArt, dir, ct);
                    if (picture is null) return null;
                    await RunAsync(opt.FfmpegPath, FrameArgs(picture, frame, isAiImage: false), dir, ct);
                    return frame;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException)
        {
            logger.LogWarning("Toma \"{Kind}: {Value}\" falló: {Msg}", r.Kind, r.Value, ex.Message);
            return null;
        }
    }

    /// <summary>Póster, personaje o arte oficial → la imagen tal cual (sin componer).</summary>
    private async Task<string?> RawMentionImageAsync(ShotReference r, IReadOnlyList<string> keyArt, string dir, CancellationToken ct) =>
        r.Kind switch
        {
            ShotKind.Poster => await mentions.PosterAsync(r.Value, dir, ct),
            ShotKind.Character => await mentions.CharacterAsync(r.Value, dir, ct),
            ShotKind.Photo => await mentions.PhotoAsync(r.Value, dir, ct),
            ShotKind.Logo => await mentions.LogoAsync(r.Names.FirstOrDefault() ?? r.Value, dir, ct),
            ShotKind.KeyArt => keyArt.LastOrDefault(), // la portada (vertical) antes que el banner
            _ => null,
        };

    // ── Voz ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Recorta solo el silencio que sobra en los bordes: deja 0,10 s al inicio y
    /// 0,15 s al final, con umbral −55 dB (debajo del aire de la voz, ~−49 dB) y
    /// fundidos de 30/60 ms. Antes cortaba a −45 dB con fundidos de 10 ms y la
    /// escena siguiente arrancaba a mitad de una respiración: click en cada empalme
    /// (prueba de oído D/E del 26-nov-2026). No toca el sonido de la voz. Mono
    /// 24 kHz, la frecuencia nativa de Qwen3. Público para tests.
    /// </summary>
    public static List<string> TrimVoiceArgs(string input, string output) =>
    [
        "-y", "-i", input, "-af",
        "silenceremove=start_periods=1:start_threshold=-55dB:start_silence=0.10," +
        "areverse,silenceremove=start_periods=1:start_threshold=-55dB:start_silence=0.15," +
        "afade=t=in:d=0.06,areverse,afade=t=in:d=0.03",
        "-ar", "24000", "-ac", "1", "-c:a", "pcm_s16le", output,
    ];

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
    public static List<string> ImageShotArgs(string image, int frames, int variant, bool flash, string output,
        bool gentle = false)
    {
        var d = frames.ToString(CultureInfo.InvariantCulture);
        // Tarjetas, logos y pósters: un acercamiento leve y centrado, así el texto
        // y los bordes no se salen del cuadro.
        var (z, x, y) = gentle ? ($"1+0.04*on/{d}", "iw/2-(iw/zoom/2)", "ih/2-(ih/zoom/2)") : (variant % 6) switch
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
    /// Toma de la pantalla final: imagen quieta (sin zoompan, así nada se mueve
    /// debajo de los elementos), desenfocada, oscurecida y con fundido desde negro.
    /// Público para tests.
    /// </summary>
    public static List<string> EndScreenArgs(string image, int frames, string output)
    {
        var d = frames.ToString(CultureInfo.InvariantCulture);
        return
        [
            "-y", "-loop", "1", "-i", image,
            "-vf", "scale=1920:1080:force_original_aspect_ratio=increase,crop=1920:1080," +
                   $"gblur=sigma=12,eq=brightness=-0.18,fps={Fps},fade=t=in:st=0:d=0.6,format=yuv420p",
            "-frames:v", d, "-an",
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
    /// Fuentes de metraje del video: las del plan (<see cref="VideoPlan.Footage"/>)
    /// o, en planes sin manifiesto, el tráiler de AniList / --clip como "trailer".
    /// </summary>
    private static List<FootageSource> FootageSources(VideoPlan plan, FootageLibrary? footage = null) =>
        plan.Footage is { Count: > 0 }
            ? plan.Footage
            : (footage?.Pools ?? []).Select(p => new FootageSource { Id = p.Id, Kind = p.Kind }).ToList();

    /// <summary>
    /// Baja (si hace falta) y corta cada fuente de metraje. Un plano que aparece en
    /// dos fuentes (el teaser y el tráiler repiten planos) queda como el mismo
    /// momento: usado en una, usado en las dos. Una fuente que falla se saltea.
    /// </summary>
    private async Task<FootageLibrary> PrepareFootageAsync(VideoPlan plan, VideoResearch? research,
        VideoRenderOptions opt, string dir, CancellationToken ct)
    {
        var pools = new List<FootagePool>();
        var kept = new List<(float[] Signature, int Moment, string Pool)>();
        if (plan.Footage is { Count: > 0 })
        {
            Directory.CreateDirectory(Path.Combine(dir, "footage"));
            foreach (var source in plan.Footage)
            {
                // El id es nombre de archivo y la url va a yt-dlp: se validan para que
                // un plan mal escrito no escriba fuera de footage/ ni pase opciones.
                if (!FootageSource.IsValidId(source.Id) || source.Url is { Length: > 0 } u && !FootageSource.IsValidUrl(u))
                {
                    logger.LogWarning("Metraje \"{Id}\" ignorado: id [a-z0-9-] y url https:// obligatorios", source.Id);
                    continue;
                }
                var file = source.File is { Length: > 0 } ? Path.GetFullPath(Path.Combine(dir, source.File)) : null;
                if (file is not null && !file.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Metraje \"{Id}\": file fuera de la carpeta del video — se ignora", source.Id);
                    file = null;
                }
                if (await PreparePoolAsync(source.Id, source.Kind, source.Url, file,
                        Path.Combine(dir, "footage"), source.Id, kept, opt, ct) is { } pool)
                    pools.Add(pool);
            }
        }
        else if (!string.IsNullOrWhiteSpace(research?.TrailerUrl)
                 && await PreparePoolAsync("trailer", "trailer", research.TrailerUrl, null, dir, "trailer", kept, opt, ct) is { } pool)
            pools.Add(pool);
        return new FootageLibrary(pools);
    }

    /// <summary>
    /// Baja un video oficial (YouTube, vía yt-dlp) y lo corta por cambios de
    /// plano: los tramos de <see cref="MinSegmentSeconds"/> o más son planos posibles y de cada uno sale un
    /// fotograma. En tráileres y teasers se descartan el principio y el final
    /// (logos, fechas, créditos); en videoclips, solo los bordes. Si algo falla,
    /// null: el video sale con las otras fuentes, o solo con imágenes.
    /// </summary>
    private async Task<FootagePool?> PreparePoolAsync(string id, string kind, string? url, string? file, string folder,
        string name, List<(float[] Signature, int Moment, string Pool)> kept, VideoRenderOptions opt, CancellationToken ct)
    {
        try
        {
            var path = file is not null && File.Exists(file) ? file
                : Directory.GetFiles(folder, $"{name}.*").FirstOrDefault(f =>
                    !f.EndsWith(".part", StringComparison.Ordinal) && !f.EndsWith(".jpg", StringComparison.Ordinal));
            if (path is null)
            {
                if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("sin archivo ni url");
                var parts = opt.YtDlpCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var ytArgs = parts.Skip(1).Concat(
                [
                    "-q", "--no-warnings", "--no-playlist",
                    "-f", "bv*[ext=mp4][height<=1080]/bv*[height<=1080]/best[height<=1080]",
                    "-o", $"{name}.%(ext)s",
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
                ytArgs.AddRange(["--", url]); // "--": la url nunca se lee como opción
                await RunAsync(parts[0], ytArgs, folder, ct, TimeSpan.FromMinutes(5));
                path = Directory.GetFiles(folder, $"{name}.*").First(f =>
                    !f.EndsWith(".part", StringComparison.Ordinal) && !f.EndsWith(".jpg", StringComparison.Ordinal));
            }

            var duration = await ProbeDurationAsync(opt, path, ct);
            var (_, err) = await RunFullAsync(opt.FfmpegPath,
                ["-hide_banner", "-i", path, "-vf", "scale=320:-2,select='gt(scene,0.3)',showinfo", "-an", "-f", "null", "-"],
                folder, ct, TimeSpan.FromMinutes(5));
            var cuts = new List<double> { 0 };
            cuts.AddRange(ShowInfoTime().Matches(err).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
            cuts.Add(duration);

            // Los créditos del staff (texto en japonés encima de la imagen) se
            // concentran al principio y al final de un tráiler: visto en el de
            // Frieren. Un videoclip cuenta su historia de punta a punta.
            var musicVideo = kind is "mv" or "opening" or "ending";
            var (head, tail) = musicVideo ? (3.0, 6.0) : (Math.Max(2, duration * 0.15), Math.Max(6, duration * 0.10));
            var pool = new FootagePool(id, kind, path, []);
            var own = new List<float[]>();
            for (var i = 0; i + 1 < cuts.Count && pool.Segments.Count < 80; i++)
            {
                var (a, b) = (cuts[i] + 0.15, cuts[i + 1] - 0.15);
                if (a < head || b > duration - tail || b - a < MinSegmentSeconds) continue;

                var still = Path.Combine(folder, $"{name}_seg_{i:000}.jpg");
                if (!File.Exists(still))
                    await RunAsync(opt.FfmpegPath,
                        ["-y", "-ss", ((a + b) / 2).ToString("0.###", CultureInfo.InvariantCulture), "-i", path,
                         "-frames:v", "1", "-q:v", "2", still], folder, ct);

                // Un tráiler vuelve varias veces al mismo lugar: si este plano se
                // parece a uno ya elegido de la misma fuente, se descarta (si no, salen
                // dos tomas iguales). Si se parece a uno de OTRA fuente, comparte su momento.
                var signature = ImageSignature(still);
                if (signature is null || own.Any(k => SignatureDistance(k, signature) < SimilarSignature)) continue;
                own.Add(signature);
                var twin = kept.FirstOrDefault(k => k.Pool != id && SignatureDistance(k.Signature, signature) < SimilarSignature);
                var moment = twin.Signature is not null ? twin.Moment : kept.Count;
                kept.Add((signature, moment, id));
                pool.Segments.Add(new FootageSegment(a, b - a, still, (a + b) / 2, moment));
            }
            logger.LogInformation("Metraje {Id} ({Kind}): {Secs:F0}s, {Segments} planos usables",
                id, kind, duration, pool.Segments.Count);
            return pool.Segments.Count == 0 ? null : pool;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning("Sin metraje {Id} ({Url}): {Msg}",
                id, url, ex.Message.Length > 300 ? ex.Message[..300] : ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Arranque de director.md: de qué fuente sale cada escena y por qué, y qué
    /// menciones pasaron a metraje porque ya se habían presentado. El loop de
    /// tomas agrega después una línea por toma.
    /// </summary>
    private static StringBuilder DirectorReport(VideoPlan plan, IReadOnlyList<FootageRoute> routes, IntroductionResult intro)
    {
        var sb = new StringBuilder("# Dirección\n\n## Fuente de cada escena\n\n");
        for (var i = 0; i < plan.Scenes.Count; i++)
        {
            var text = plan.Scenes[i].Narration;
            sb.Append(CultureInfo.InvariantCulture,
                $"- {i + 1}: **{string.Join(" + ", routes[i].Ids)}** ({routes[i].Reason}) — {(text.Length > 70 ? text[..70] + "…" : text)}\n");
        }
        sb.Append("\n## Presentaciones repetidas\n\n");
        foreach (var c in intro.Changes)
            sb.Append(CultureInfo.InvariantCulture,
                $"- esc {c.Scene} «{c.At}»: `{c.Original}` → {(c.Now is null ? "metraje" : $"`{c.Now}`")} ({c.Reason})\n");
        if (intro.Changes.Count == 0) sb.Append("- ninguna\n");
        sb.Append("\n## Tomas\n\n```\n");
        return sb;
    }

    /// <summary>
    /// Plano más corto que se guarda. Los tráileres nuevos cortan muy rápido (el de
    /// Edgerunners 2: 8 de 140 planos pasan de 1,2 s): un plano corto sirve como
    /// fotograma, y como clip solo si cubre el 60% de la toma.
    /// </summary>
    public const double MinSegmentSeconds = 0.6;

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

    private static string Metadata(VideoPlan plan, TimeSpan duration, IReadOnlyList<string> musicCredits) => new StringBuilder()
        .AppendLine($"TÍTULO: {plan.Title}")
        .AppendLine($"DURACIÓN: {duration:mm\\:ss}")
        .AppendLine().AppendLine("DESCRIPCIÓN:").AppendLine(plan.Description)
        .AppendLine().AppendLine("Fuentes:").AppendLine(string.Join("\n", plan.Sources.Select(s => "- " + s)))
        // CC BY exige el crédito: va en la descripción del video.
        .Append(musicCredits.Count == 0 ? "" : "\n" + string.Join("\n", musicCredits) + "\n")
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
                text = VideoPlan.ForSpeech(s.Narration, plan.Pronunciations),
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
