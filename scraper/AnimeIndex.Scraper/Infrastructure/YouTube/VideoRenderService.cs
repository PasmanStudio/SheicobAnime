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
    string QwenBackend = "kaggle")
{
    public bool UsesClonedVoice => !string.IsNullOrWhiteSpace(CloneRef);
}

/// <summary>
/// <see cref="VideoPlan"/> → video.mp4 horizontal 1080p:
///   voz por escena (Edge TTS, voces neuronales es-MX, sin key),
///   una imagen por escena (arte oficial de AniList o Pollinations),
///   zoom/paneo lento sobre cada imagen (FFmpeg zoompan),
///   subtítulos quemados con los tiempos que devuelve el propio TTS.
///
/// El MP4 queda en la carpeta de salida para revisarlo; acá no se sube nada.
/// Las imágenes ya bajadas (raw_NN.jpg) se reutilizan si se re-renderiza en la
/// misma carpeta: para regenerarlas, borrarlas.
/// </summary>
public sealed partial class VideoRenderService(
    IHttpClientFactory httpFactory,
    ILogger<VideoRenderService> logger)
{
    private const int Fps = 30;
    private const double ScenePadSeconds = 0.4;

    public async Task<string> RenderAsync(
        VideoPlan plan, VideoResearch? research, VideoRenderOptions opt, CancellationToken ct)
    {
        Directory.CreateDirectory(opt.OutputDir);
        var dir = Path.GetFullPath(opt.OutputDir);
        await File.WriteAllTextAsync(Path.Combine(dir, "plan.json"), plan.ToJson(), ct);

        var keyArt = await DownloadKeyArtAsync(research, dir, ct);
        var segments = new List<string>();
        var subtitles = new List<(string Srt, TimeSpan Offset)>();
        var elapsed = TimeSpan.Zero;
        var keyArtTurn = 0;

        // Pollinations da 1 imagen cada 45 s por IP: se generan en segundo plano,
        // en orden, mientras el loop hace voz y segmentos de las escenas previas.
        bool UsesKeyArt(VideoScene s) =>
            s.Visual.Equals("keyart", StringComparison.OrdinalIgnoreCase) && keyArt.Count > 0;
        var images = plan.Scenes
            .Select(_ => new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToList();
        var imageJob = Task.Run(async () =>
        {
            for (var i = 0; i < plan.Scenes.Count; i++)
            {
                try
                {
                    images[i].SetResult(UsesKeyArt(plan.Scenes[i])
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

        for (var i = 0; i < plan.Scenes.Count; i++)
        {
            var scene = plan.Scenes[i];
            var n = (i + 1).ToString("00", CultureInfo.InvariantCulture);
            var sw = Stopwatch.StartNew();

            // 1. Voz + subtítulos de la escena
            var audio = Path.Combine(dir, opt.UsesClonedVoice ? $"scene_{n}.wav" : $"scene_{n}.mp3");
            var srt = Path.Combine(dir, $"scene_{n}.srt");
            double seconds;
            if (opt.UsesClonedVoice)
            {
                // Qwen no devuelve tiempos: se reparten por frase según su largo.
                var voice = await ProbeDurationAsync(opt, audio, ct);
                await File.WriteAllTextAsync(srt, EstimateSrt(scene.Narration, voice), ct);
                seconds = voice + ScenePadSeconds;
            }
            else
            {
                await RunTtsAsync(opt, scene.Narration, audio, srt, ct);
                seconds = await ProbeDurationAsync(opt, audio, ct) + ScenePadSeconds;
            }

            // 2. Imagen → cuadro 1920x1080
            var raw = UsesKeyArt(scene) ? keyArt[keyArtTurn++ % keyArt.Count] : await images[i].Task;
            raw ??= keyArt.Count > 0 ? keyArt[i % keyArt.Count] : null;

            var frame = Path.Combine(dir, $"frame_{n}.jpg");
            await RunAsync(opt.FfmpegPath, FrameArgs(raw, frame, isAiImage: raw is not null && Path.GetFileName(raw).StartsWith("raw_", StringComparison.Ordinal)), dir, ct);

            // 3. Segmento con movimiento + voz
            var segment = Path.Combine(dir, $"seg_{n}.mp4");
            await RunAsync(opt.FfmpegPath, SegmentArgs(frame, audio, seconds, i, segment), dir, ct);
            segments.Add(segment);

            subtitles.Add((await File.ReadAllTextAsync(srt, ct), elapsed));
            elapsed += TimeSpan.FromSeconds(seconds);
            logger.LogInformation("Escena {N}/{Total}: {Secs:F1}s, visual={Visual} ({Ms} ms)",
                n, plan.Scenes.Count, seconds, raw is null ? "fondo" : Path.GetFileName(raw), sw.ElapsedMilliseconds);
        }

        await imageJob;

        // 4. Unir segmentos y quemar subtítulos
        await File.WriteAllLinesAsync(Path.Combine(dir, "segments.txt"),
            segments.Select(s => $"file '{Path.GetFileName(s)}'"), ct);
        await RunAsync(opt.FfmpegPath,
            ["-y", "-f", "concat", "-safe", "0", "-i", "segments.txt", "-c", "copy", "body.mp4"], dir, ct);

        await File.WriteAllTextAsync(Path.Combine(dir, "subs.srt"), MergeSrt(subtitles), new UTF8Encoding(false), ct);
        var final = Path.Combine(dir, "video.mp4");
        await RunAsync(opt.FfmpegPath,
        [
            "-y", "-i", "body.mp4",
            // Ruta relativa a propósito: en Windows "C:" rompe el parseo del filtro.
            "-vf", "subtitles=subs.srt:force_style='FontName=Arial,FontSize=15,Bold=1,Outline=2,Shadow=0,MarginV=28'",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "copy", "-movflags", "+faststart", "video.mp4",
        ], dir, ct);

        await File.WriteAllTextAsync(Path.Combine(dir, "youtube.txt"), Metadata(plan, elapsed), ct);
        File.Copy(Path.Combine(dir, "frame_01.jpg"), Path.Combine(dir, "thumbnail.jpg"), overwrite: true);
        logger.LogInformation("Video listo: {Path} ({Duration:mm\\:ss})", final, elapsed);
        return final;
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

    /// <summary>
    /// Un cuadro fijo + la voz → segmento con movimiento lento (Ken Burns).
    /// Se escala a 4K antes del zoompan para que el movimiento no tiemble. El
    /// movimiento rota entre acercar, alejar y paneo para que no sea monótono.
    /// </summary>
    public static List<string> SegmentArgs(string frame, string audio, double seconds, int index, string output)
    {
        var frames = (int)Math.Ceiling(seconds * Fps);
        var d = frames.ToString(CultureInfo.InvariantCulture);
        var (z, x, y) = (index % 3) switch
        {
            0 => ($"1+0.12*on/{d}", "iw/2-(iw/zoom/2)", "ih/2-(ih/zoom/2)"),
            1 => ($"1.12-0.12*on/{d}", "iw/2-(iw/zoom/2)", "ih/2-(ih/zoom/2)"),
            _ => ("1.1", $"(iw-iw/zoom)*on/{d}", "ih/2-(ih/zoom/2)"),
        };
        var secs = seconds.ToString("0.###", CultureInfo.InvariantCulture);
        var filter =
            $"[0:v]scale=3840:2160,zoompan=z='{z}':x='{x}':y='{y}':d={d}:s=1920x1080:fps={Fps},format=yuv420p[v];" +
            $"[1:a]loudnorm=I=-16:TP=-1.5:LRA=11,apad=pad_dur={ScenePadSeconds.ToString(CultureInfo.InvariantCulture)}[a]";
        return
        [
            "-y", "-i", frame, "-i", audio, "-filter_complex", filter,
            "-map", "[v]", "-map", "[a]", "-t", secs,
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-r", Fps.ToString(CultureInfo.InvariantCulture),
            "-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2", output,
        ];
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
        return await stdout;
    }
}
