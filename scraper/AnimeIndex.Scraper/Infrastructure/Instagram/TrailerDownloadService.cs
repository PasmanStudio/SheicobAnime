using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>
/// Descarga un tráiler/PV de YouTube con yt-dlp para usarlo de fondo en el
/// Reel de noticias (formato "video + titular" — los PV son material
/// promocional que los estudios publican para difusión; va MUTEADO con
/// nuestra música encima).
///
/// Best-effort por diseño: yt-dlp puede faltar (dev local), YouTube puede
/// bloquear IPs de datacenter, el video puede estar region-locked — TODO
/// devuelve null y el reel cae al slideshow de imágenes. Nunca rompe nada.
///
/// El caller es dueño del archivo devuelto (borrar el directorio al terminar).
/// </summary>
public class TrailerDownloadService(
    InstagramSettings settings,
    ILogger<TrailerDownloadService> logger)
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Descarga el video (solo stream de video, ≤720p — el audio se descarta
    /// igual) y devuelve la ruta del MP4, o null si algo falló.
    /// </summary>
    public async Task<string?> DownloadAsync(string videoUrl, CancellationToken ct = default)
    {
        var workDir = Path.Join(Path.GetTempPath(), $"ig-trailer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var outputPath = Path.Join(workDir, "trailer.mp4");

        // Cookies de una sesión logueada: es lo que realmente evade el "confirm
        // you're not a bot" desde IPs de datacenter (el PO token solo no alcanza).
        // El workflow escribe el secret a un archivo; solo lo pasamos si existe
        // (sin secret cargado → sin --cookies, best-effort como siempre).
        var cookiesArg = !string.IsNullOrWhiteSpace(settings.YtDlpCookiesPath)
                         && File.Exists(settings.YtDlpCookiesPath)
            ? $"--cookies \"{settings.YtDlpCookiesPath}\" "
            : string.Empty;

        var psi = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(settings.YtDlpPath) ? "yt-dlp" : settings.YtDlpPath,
            // Solo video H.264 ≤720p (sin audio: se mutea igual y baja más rápido);
            // fallbacks progresivos por si el formato exacto no existe.
            // --extractor-args player_client=...: YouTube tira "confirm you're not a
            // bot" contra IPs de datacenter (GitHub Actions). El fix real es --cookies
            // (arriba); el PO token provider (bgutil) + los clientes web complementan.
            // Configurable vía Instagram__YtDlpPlayerClients (default "web_safari,tv").
            Arguments =
                "-f \"bv*[height<=720][ext=mp4]/bv*[height<=720]/best[height<=720]/best\" " +
                $"--extractor-args \"youtube:player_client={settings.YtDlpPlayerClients}\" " +
                cookiesArg +
                "--no-playlist --max-filesize 150M --socket-timeout 20 " +
                $"-o \"{outputPath}\" \"{videoUrl}\"",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");

            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(DownloadTimeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* ya salió */ }
                catch (System.ComponentModel.Win32Exception) { /* best-effort */ }
                logger.LogWarning("yt-dlp timeout ({Min} min) para {Url} — reel cae a slideshow",
                    DownloadTimeout.TotalMinutes, videoUrl);
                CleanUp(workDir);
                return null;
            }

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                var tail = stderr.ToString();
                if (tail.Length > 500) tail = tail[^500..];
                logger.LogWarning("yt-dlp falló (exit {Code}) para {Url}: {Err} — reel cae a slideshow",
                    process.ExitCode, videoUrl, tail);
                CleanUp(workDir);
                return null;
            }

            var mb = new FileInfo(outputPath).Length / 1024.0 / 1024.0;
            logger.LogInformation("Tráiler descargado: {Url} → {Mb:F1} MB", videoUrl, mb);
            return outputPath;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // yt-dlp no instalado (dev local) — el workflow de CI sí lo instala
            logger.LogInformation("yt-dlp no encontrado ('{Path}') — reel cae a slideshow", psi.FileName);
            CleanUp(workDir);
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Descarga de tráiler falló para {Url} — reel cae a slideshow", videoUrl);
            CleanUp(workDir);
            return null;
        }
    }

    /// <summary>Borra el directorio temporal del clip (llamar al terminar de usarlo).</summary>
    public static void CleanUp(string pathInsideWorkDir)
    {
        try
        {
            var dir = File.Exists(pathInsideWorkDir)
                ? Path.GetDirectoryName(pathInsideWorkDir)
                : pathInsideWorkDir;
            if (dir is not null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }
}
