using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>
/// Thrown when ffmpeg is not installed/reachable — the publisher degrades to
/// image-only posts instead of failing the run.
/// </summary>
public class FfmpegNotAvailableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

/// <summary>
/// Generates a 9:16 "motion card" MP4 (1080×1920) from a still card image:
/// zoom lento estilo Ken Burns + fade-in, pista de audio AAC silenciosa.
/// Cumple los requisitos de Reels de la Graph API: H.264 yuv420p, closed GOP,
/// moov atom al frente (+faststart), 3s–15min, ≤300MB.
///
/// Usa el ffmpeg del sistema (preinstalado en ubuntu-latest). Sin ffmpeg
/// (p. ej. dev local en Windows) lanza FfmpegNotAvailableException y el
/// publisher hace fallback a la story/post de imagen de siempre.
/// </summary>
public class InstagramVideoService(
    InstagramSettings settings,
    ILogger<InstagramVideoService> logger)
{
    private const int Fps = 30;
    private const int OutWidth = 1080;
    private const int OutHeight = 1920;
    // Zoom final del paneo (10% de acercamiento a lo largo del clip)
    private const double MaxZoom = 1.10;

    /// <summary>
    /// Renders the motion-card MP4 (1080×1920) and returns the bytes.
    /// Con <paramref name="overlayPng"/> anima en capas: Ken Burns en el fondo
    /// y el bloque de texto entrando con slide-up + fade (easing cúbico) —
    /// motion graphics de verdad, texto siempre nítido. Sin overlay, zoom plano
    /// sobre la tarjeta completa. Con <paramref name="musicMp3"/> mezcla el
    /// track (fade in/out + loudnorm); sin música, pista AAC silenciosa.
    /// </summary>
    public async Task<byte[]> GenerateMotionCardAsync(
        byte[] cardImageBytes,
        byte[]? overlayPng = null,
        byte[]? musicMp3 = null,
        int musicStartSeconds = 0,
        CancellationToken ct = default)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"ig-reel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var inputPath = Path.Combine(workDir, "card.png");
        var outputPath = Path.Combine(workDir, "reel.mp4");
        string? overlayPath = null;
        string? musicPath = null;

        try
        {
            await File.WriteAllBytesAsync(inputPath, cardImageBytes, ct);
            if (overlayPng is not null)
            {
                overlayPath = Path.Combine(workDir, "overlay.png");
                await File.WriteAllBytesAsync(overlayPath, overlayPng, ct);
            }
            if (musicMp3 is not null)
            {
                musicPath = Path.Combine(workDir, "music.mp3");
                await File.WriteAllBytesAsync(musicPath, musicMp3, ct);
            }

            var args = BuildFfmpegArguments(
                inputPath, outputPath, settings.ReelDurationSeconds, musicPath, musicStartSeconds, overlayPath);
            await RunFfmpegAsync(args, ct);

            var bytes = await File.ReadAllBytesAsync(outputPath, ct);
            logger.LogInformation("Generated motion-card reel: {Seconds}s, {Mb:F1} MB",
                settings.ReelDurationSeconds, bytes.Length / 1024.0 / 1024.0);
            return bytes;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Público + static para poder testear la construcción de argumentos sin ffmpeg.
    /// Inputs: 0 = fondo, [1 = overlay de texto si hay], último = audio.
    /// Con <paramref name="overlayPath"/> el texto entra con slide-up + fade
    /// (easing cúbico ease-out) sobre el Ken Burns del fondo. Con
    /// <paramref name="musicPath"/> el track entra desde
    /// <paramref name="musicStartSeconds"/> con fade in/out y loudnorm; sin
    /// música, pista silenciosa (anullsrc).
    /// </summary>
    public static string BuildFfmpegArguments(
        string inputPath, string outputPath, int durationSeconds,
        string? musicPath = null, int musicStartSeconds = 0, string? overlayPath = null)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var frames = durationSeconds * Fps;
        // El paso de zoom por frame para llegar a MaxZoom justo al final del clip
        var zoomStep = (MaxZoom - 1.0) / frames;

        // zoompan tiembla con inputs chicos: se pre-escala 2× (lanczos) y el
        // filtro recorta la ventana 1080×1920. fade-in de 0.6s al arranque.
        var backgroundFilter =
            $"[0:v]scale={OutWidth * 2}:{OutHeight * 2}:flags=lanczos," +
            $"zoompan=z='min(1+on*{zoomStep.ToString("0.00000000", inv)},{MaxZoom.ToString("0.00", inv)})'" +
            $":d={frames}:x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':s={OutWidth}x{OutHeight}:fps={Fps}," +
            "fade=t=in:st=0:d=0.6";

        string videoFilter, overlayInput;
        if (overlayPath is not null)
        {
            // Motion graphics: el bloque de texto entra deslizándose 80px hacia
            // arriba con easing cúbico (ease-out) + fade, arrancando a los 0.5s.
            // p = progreso 0→1 en 0.9s; y = pow(1-p,3)*80 (80px → 0 suavizado).
            overlayInput = $"-loop 1 -i \"{overlayPath}\"";
            videoFilter =
                backgroundFilter + "[bg];" +
                "[1:v]format=rgba,fade=t=in:st=0.5:d=0.8:alpha=1[ov];" +
                "[bg][ov]overlay=x=0:y='pow(1-min(1,max(0,(t-0.5)/0.9)),3)*80'," +
                "format=yuv420p[v]";
        }
        else
        {
            overlayInput = "";
            videoFilter = backgroundFilter + ",format=yuv420p[v]";
        }

        // El índice del input de audio depende de si hay overlay
        var audioIdx = overlayPath is not null ? 2 : 1;

        string audioInput, filter, audioMap;
        if (musicPath is not null)
        {
            // Track real: fade-in corto, fade-out al cierre, loudnorm al nivel
            // estándar de social media (-16 LUFS)
            var fadeOutStart = Math.Max(0, durationSeconds - 1.5).ToString("0.0#", inv);
            audioInput = $"-ss {musicStartSeconds} -t {durationSeconds} -i \"{musicPath}\"";
            filter = videoFilter + ";" +
                $"[{audioIdx}:a]afade=t=in:st=0:d=0.8,afade=t=out:st={fadeOutStart}:d=1.5," +
                "loudnorm=I=-16:TP=-1.5:LRA=11[a]";
            audioMap = "-map [a]";
        }
        else
        {
            // Pista de audio silenciosa: algunos clientes de IG tratan mal los
            // videos sin stream de audio, y una pista muda no tiene copyright.
            audioInput = $"-f lavfi -t {durationSeconds} -i anullsrc=channel_layout=stereo:sample_rate=44100";
            filter = videoFilter;
            audioMap = $"-map {audioIdx}:a";
        }

        return string.Join(' ',
            "-y",
            $"-i \"{inputPath}\"",
            overlayInput,
            audioInput,
            $"-filter_complex \"{filter}\"",
            $"-map [v] {audioMap}",
            $"-t {durationSeconds} -r {Fps}",
            // closed GOP + keyframe cada 2s, como piden las specs de Reels
            "-c:v libx264 -profile:v high -preset medium -flags +cgop -g 60 -sc_threshold 0",
            "-b:v 6M -maxrate 8M -bufsize 12M",
            "-c:a aac -b:a 128k -ar 44100",
            "-movflags +faststart",
            "-shortest",
            $"\"{outputPath}\"");
    }

    private async Task RunFfmpegAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(settings.FfmpegPath) ? "ffmpeg" : settings.FfmpegPath,
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new FfmpegNotAvailableException("Process.Start returned null for ffmpeg");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new FfmpegNotAvailableException(
                $"ffmpeg no encontrado ('{psi.FileName}') — instalalo o configurá Instagram__FfmpegPath", ex);
        }

        using (process)
        {
            // stderr es donde ffmpeg escribe su log — se acumula para diagnóstico
            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ya murió */ }
                throw new InvalidOperationException("ffmpeg no terminó dentro de los 3 minutos");
            }

            if (process.ExitCode != 0)
            {
                var tail = stderr.ToString();
                if (tail.Length > 2000) tail = tail[^2000..];
                throw new InvalidOperationException($"ffmpeg falló (exit {process.ExitCode}): {tail}");
            }
        }
    }
}
