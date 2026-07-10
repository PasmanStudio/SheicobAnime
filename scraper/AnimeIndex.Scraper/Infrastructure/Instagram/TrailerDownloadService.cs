using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>Tráiler elegido: URL watch + duración (para armar el reel).</summary>
public sealed record TrailerCandidate(string Url, double DurationSeconds);

/// <summary>
/// Descarga un tráiler/PV de YouTube con yt-dlp para usarlo en el Reel de
/// noticias (formato "video + titular" — los PV son material promocional que
/// los estudios publican para difusión; va CON SU AUDIO ORIGINAL, que es lo
/// que la gente quiere escuchar). También BUSCA el tráiler en YouTube
/// (ytsearch) cuando el artículo no embebe ninguno — que es el caso típico.
///
/// Best-effort por diseño: yt-dlp puede faltar (dev local), YouTube puede
/// bloquear IPs de datacenter, el video puede estar region-locked — TODO
/// devuelve null y el reel cae al slideshow de imágenes. Nunca rompe nada.
///
/// El caller es dueño del archivo devuelto (borrar el directorio al terminar).
/// </summary>
public partial class TrailerDownloadService(
    InstagramSettings settings,
    ILogger<TrailerDownloadService> logger)
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(75);

    // Cuántos resultados de búsqueda evaluar por query
    private const int SearchResults = 6;

    // Separador improbable en títulos para el --print de yt-dlp (un "|" solo
    // aparece en títulos de anime todo el tiempo)
    private const string FieldSeparator = "|~|";

    /// <summary>
    /// Descarga el video CON su audio original (≤720p — el sonido del tráiler
    /// es parte del atractivo del reel) y devuelve la ruta del MP4, o null si
    /// algo falló.
    /// </summary>
    public async Task<string?> DownloadAsync(string videoUrl, CancellationToken ct = default)
    {
        var workDir = Path.Join(Path.GetTempPath(), $"ig-trailer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var outputPath = Path.Join(workDir, "trailer.mp4");

        var psi = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(settings.YtDlpPath) ? "yt-dlp" : settings.YtDlpPath,
            // Video H.264 ≤720p + su pista de audio (yt-dlp los muxea con el
            // ffmpeg del sistema); fallbacks progresivos por si el formato
            // exacto no existe. player_client=android_vr + salida por WARP
            // (ProxyArg): la ÚNICA combinación que pasa el bot-check de YouTube
            // desde GitHub Actions (matriz probada en vivo jul-2026 — ojo: las
            // cookies ROMPEN a android_vr, no agregarlas). Configurable vía
            // Instagram__YtDlpPlayerClients.
            Arguments =
                "-f \"bv*[height<=720][ext=mp4]+ba[ext=m4a]/bv*[height<=720]+ba/b[height<=720]/best\" " +
                "--merge-output-format mp4 " +
                $"--extractor-args \"youtube:player_client={settings.YtDlpPlayerClients}\" " +
                ProxyArg() +
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

    // ── Búsqueda del tráiler en YouTube ──────────────────────────────────────

    /// <summary>
    /// Busca el tráiler en YouTube (ytsearch de yt-dlp, sin descargar nada) y
    /// devuelve el mejor candidato (URL + duración), o null si ninguno da
    /// confianza. Mismo best-effort que la descarga: cualquier fallo → null.
    /// </summary>
    public async Task<TrailerCandidate?> SearchAsync(string query, CancellationToken ct = default)
    {
        // Las comillas romperían el parseo de argumentos del proceso
        var sanitized = query.Replace('"', ' ').Trim();
        if (sanitized.Length == 0) return null;

        var psi = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(settings.YtDlpPath) ? "yt-dlp" : settings.YtDlpPath,
            // --flat-playlist: solo metadata de los resultados (rápido, una
            // sola llamada); la selección del mejor candidato es nuestra.
            Arguments =
                $"\"ytsearch{SearchResults}:{sanitized}\" " +
                $"--flat-playlist --print \"%(id)s{FieldSeparator}%(duration)s{FieldSeparator}%(title)s{FieldSeparator}%(channel)s\" " +
                $"--extractor-args \"youtube:player_client={settings.YtDlpPlayerClients}\" " +
                ProxyArg() +
                "--no-warnings --socket-timeout 20",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(SearchTimeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* ya salió */ }
                catch (System.ComponentModel.Win32Exception) { /* best-effort */ }
                logger.LogWarning("yt-dlp search timeout para \"{Query}\" — reel cae a slideshow", query);
                return null;
            }

            var lines = stdout.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (process.ExitCode != 0 && lines.Length == 0)
            {
                var tail = stderr.ToString();
                if (tail.Length > 400) tail = tail[^400..];
                logger.LogWarning("yt-dlp search falló (exit {Code}) para \"{Query}\": {Err}",
                    process.ExitCode, query, tail);
                return null;
            }

            var best = PickBestSearchResult(lines);
            if (best is null)
            {
                logger.LogInformation(
                    "Búsqueda de tráiler sin candidato confiable en español para \"{Query}\" ({Count} resultados)",
                    query, lines.Length);
                return null;
            }

            logger.LogInformation("Tráiler encontrado por búsqueda: {Id} ({Dur}s) para \"{Query}\"",
                best.Value.Id, best.Value.DurationSeconds, query);
            return new TrailerCandidate(
                $"https://www.youtube.com/watch?v={best.Value.Id}", best.Value.DurationSeconds);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            logger.LogInformation("yt-dlp no encontrado ('{Path}') — sin búsqueda de tráiler", psi.FileName);
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Búsqueda de tráiler falló para \"{Query}\"", query);
            return null;
        }
    }

    /// <summary>
    /// Elige el mejor resultado de búsqueda (líneas "id|~|duración|~|título|~|canal"
    /// del --print de yt-dlp). REQUISITO DE IDIOMA (pedido del usuario): el video
    /// tiene que estar en español (doblaje latino o subtítulos incrustados) — la
    /// audiencia es LATAM y el audio del tráiler va muteado, así que lo que importa
    /// es el texto en pantalla. Los canales tipo "Crunchyroll en Español" suben la
    /// versión doblada/subtitulada de casi todos los tráilers grandes. Además exige
    /// señal de tráiler en el título o canal oficial. Sin candidato confiable EN
    /// ESPAÑOL devuelve null y el reel cae al slideshow: mejor sin video que con
    /// el video equivocado o en japonés. Público estático para tests.
    /// </summary>
    public static (string Id, double DurationSeconds)? PickBestSearchResult(IReadOnlyList<string> printedLines)
    {
        (string Id, double Duration)? best = null;
        var bestScore = -1;

        foreach (var parts in printedLines.Select(line => line.Split(FieldSeparator)))
        {
            if (parts.Length < 4) continue;

            var id = parts[0].Trim();
            if (id.Length < 6) continue;
            double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var duration);
            var title = parts[2].ToLowerInvariant();
            var channel = parts[3].ToLowerInvariant();

            // Requisito de idioma: sin señal de español en título o canal, afuera
            if (!SpanishRegex().IsMatch(title) && !SpanishRegex().IsMatch(channel)) continue;
            // Contenido de fans (reacciones/reviews/resúmenes/trailers falsos): nunca
            if (FanContentRegex().IsMatch(title)) continue;
            // Más de 6 min no es un tráiler: episodio completo, compilado, live
            if (duration > 360) continue;

            var score = 0;
            if (TrailerWordRegex().IsMatch(title)) score += 4;
            // El upload del distribuidor oficial gana aunque el título no diga
            // "trailer"; a igual puntaje, el orden de relevancia de YouTube
            // desempata (el primero se queda).
            if (OfficialChannelRegex().IsMatch(channel)) score += 6;
            else if (title.Contains("official") || title.Contains("oficial")
                     || channel.Contains("official"))
                score += 2;
            if (duration is >= 10 and <= 300) score += 2;  // teasers arrancan en ~15s

            if (score > bestScore)
            {
                bestScore = score;
                best = (id, duration);
            }
        }

        // Sin keyword de tráiler NI canal oficial (score < 4) no hay confianza
        return bestScore >= 4 ? best : null;
    }

    // trailer/teaser/PV/avance en varios idiomas; \b evita falsos positivos
    // (p. ej. "pvp"). 予告/特報/ティザー son los usos japoneses estándar.
    [GeneratedRegex(@"\b(trailer|tráiler|teaser|avance|pv|promo)\b|予告|特報|ティザー", RegexOptions.IgnoreCase)]
    private static partial Regex TrailerWordRegex();

    // Señal de que el video está en español: doblaje latino o subtítulos
    // incrustados ("Crunchyroll en Español", "TRÁILER OFICIAL (Doblaje latino)",
    // "en español", "subtitulado"…). "Anime Onegai" es distribuidor LATAM.
    // "spanish": YouTube traduce los nombres de canal según la región del
    // requester (visto en vivo vía WARP: "Crunchyroll in Spanish").
    [GeneratedRegex(@"españ|espanol|spanish|castellano|latino|latam|doblaje|doblad|subtitulad|sub\.? esp|onegai|tráiler|avance", RegexOptions.IgnoreCase)]
    private static partial Regex SpanishRegex();

    [GeneratedRegex(@"\b(reaction|reacci[oó]n|review|rese[ñn]a|an[aá]lisis|analysis|explicado|explained|resumen|recap|amv|cosplay|theory|teor[ií]a|concept|fan[ -]?made)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FanContentRegex();

    // Distribuidores/estudios que suben los PV reales. La lista no necesita ser
    // exhaustiva: es solo un bonus — el título con "trailer/teaser" alcanza solo.
    [GeneratedRegex(@"aniplex|crunchyroll|toho|kadokawa|avex|toei|bandai|netflix|warner|pony canyon|king records|muse|ani-one|shueisha|kodansha|square enix|ufotable|mappa|wit studio|cloverworks|a-1 pictures|bones|kyoto animation|remow|tms", RegexOptions.IgnoreCase)]
    private static partial Regex OfficialChannelRegex();

    /// <summary>
    /// Valida que un video puntual (p. ej. el embebido en el artículo fuente)
    /// cumpla las MISMAS reglas que los resultados de búsqueda — en particular
    /// el requisito de español: kudasai suele embeber el PV japonés, y ese ya
    /// no sirve. Devuelve el candidato (URL + duración) si pasa, null si no
    /// (→ se busca la versión latina, o slideshow).
    /// </summary>
    public async Task<TrailerCandidate?> ValidateAsync(string videoUrl, CancellationToken ct = default)
    {
        var line = await RunYtDlpPrintAsync(
            $"--skip-download --print \"%(id)s{FieldSeparator}%(duration)s{FieldSeparator}%(title)s{FieldSeparator}%(channel)s\" " +
            $"--extractor-args \"youtube:player_client={settings.YtDlpPlayerClients}\" " +
            ProxyArg() +
            $"--no-warnings --socket-timeout 20 \"{videoUrl}\"", ct);

        if (line is null) return null;

        var best = PickBestSearchResult([line]);
        if (best is null)
        {
            logger.LogInformation(
                "Tráiler embebido descartado (sin señal de español/tráiler): {Line}",
                line.Length > 120 ? line[..120] : line);
            return null;
        }
        return new TrailerCandidate(videoUrl, best.Value.DurationSeconds);
    }

    /// <summary>Corre yt-dlp esperando UNA línea por stdout; null si falla.</summary>
    private async Task<string?> RunYtDlpPrintAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(settings.YtDlpPath) ? "yt-dlp" : settings.YtDlpPath,
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");

            var stdout = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* ya salió */ }
                catch (System.ComponentModel.Win32Exception) { /* best-effort */ }
                return null;
            }

            var line = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return process.ExitCode == 0 ? line : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "yt-dlp print falló");
            return null;
        }
    }

    /// <summary>
    /// Proxy de salida para TODOS los comandos de yt-dlp. YouTube bloquea por IP
    /// a los runners de GitHub (confirmado jul-2026: 21/21 configuraciones
    /// cliente×cookies fallaron con el bot-check); el workflow levanta Cloudflare
    /// WARP (wgcf + wireproxy → SOCKS5 local) y pasa la URL acá. Vacío = directo.
    /// </summary>
    private string ProxyArg() =>
        !string.IsNullOrWhiteSpace(settings.YtDlpProxy)
            ? $"--proxy \"{settings.YtDlpProxy}\" "
            : string.Empty;

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
