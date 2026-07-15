using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>
/// Tráiler elegido: URL watch + duración (para armar el reel).
/// <paramref name="SubtitlesPath"/>: archivo .vtt con subtítulos MANUALES en
/// español para quemar en el video — solo cuando el tráiler no está en español
/// pero tiene subs oficiales (el caller lo limpia con CleanUp al terminar).
/// </summary>
public sealed record TrailerCandidate(string Url, double DurationSeconds, string? SubtitlesPath = null);

/// <summary>
/// Qué tipo de video presenta la noticia. Define las palabras clave que dan
/// confianza en la búsqueda y si aplica el requisito de español:
///   • Trailer  — tráiler/teaser/PV: tiene diálogo/texto, se exige versión en
///                español (o subs manuales para quemar).
///   • ThemeSong — opening/ending/video musical: ES una canción (japonesa por
///                naturaleza), el idioma no aplica; solo uploads oficiales.
///   • Short    — corto animado/video especial/aniversario: el video ES la
///                noticia; se acepta el original (con subs es si existen).
/// </summary>
public enum NewsVideoKind { Trailer, ThemeSong, Short }

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
    /// Busca el video en YouTube (ytsearch de yt-dlp, sin descargar nada) y
    /// devuelve el mejor candidato (URL + duración), o null si ninguno da
    /// confianza. <paramref name="requireSpanish"/>=false relaja el requisito
    /// de idioma (tráiler oficial en cualquier idioma al que después se le
    /// queman subtítulos es, o un opening/corto donde el idioma no aplica —
    /// en ese modo el upload tiene que ser OFICIAL). <paramref name="kind"/>
    /// define qué palabras clave dan confianza. <paramref name="subject"/> es
    /// el nombre de la obra: el resultado TIENE que referenciarla (ver
    /// PickBestSearchResult) — sin esto, YouTube rellena las búsquedas sin
    /// buen match con tráilers populares de cine que pasan todos los otros
    /// filtros. Mismo best-effort que la descarga.
    /// </summary>
    public async Task<TrailerCandidate?> SearchAsync(
        string query, bool requireSpanish = true,
        NewsVideoKind kind = NewsVideoKind.Trailer, string? subject = null,
        CancellationToken ct = default)
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

            var best = PickBestSearchResult(lines, requireSpanish, kind, subject ?? query);
            if (best is null)
            {
                logger.LogInformation(
                    "Búsqueda de {Kind} sin candidato confiable{Lang} para \"{Query}\" ({Count} resultados)",
                    kind, requireSpanish ? " en español" : "", query, lines.Length);
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
    /// del --print de yt-dlp). REQUISITO DE IDIOMA (pedido del usuario): el tráiler
    /// tiene que estar en español (doblaje latino o subtítulos incrustados) — la
    /// audiencia es LATAM. Los canales tipo "Crunchyroll en Español" suben la
    /// versión doblada/subtitulada de casi todos los tráilers grandes. Además exige
    /// la palabra clave del tipo de video en el título o canal oficial. Sin
    /// candidato confiable devuelve null y el reel cae al slideshow: mejor sin
    /// video que con el video equivocado.
    ///
    /// <paramref name="requireSpanish"/>=false relaja SOLO el idioma (tráiler con
    /// subs a quemar, o un opening/corto donde el idioma no aplica) pero a cambio
    /// EXIGE señal de upload oficial: sin el filtro de español, un video fan en
    /// inglés titulado "trailer" quedaría primero (pasó en prod: reel publicado
    /// con un video de comentario en inglés — YouTube ADEMÁS auto-traduce títulos
    /// según la región del requester, así que el título que vemos vía WARP puede
    /// venir "en español" aunque el video no lo esté).
    ///
    /// <paramref name="subject"/>: el nombre de la obra (o la query completa —
    /// las palabras de relleno son stopwords). REGLA DE RELEVANCIA: la mayoría
    /// estricta de sus tokens significativos tiene que aparecer en el título o
    /// canal del resultado. Sin esto, cuando la obra no tiene tráiler en
    /// español YouTube rellena con tráilers de CINE en español que pasan TODOS
    /// los demás filtros (pasó en prod 14-15 jul: "Project X" para Tsugumi
    /// Project, "Faraway Downs" para From Far Away, "La Piel Que Habito" y
    /// "Contratiempo" — todos de canales Warner oficiales, con "tráiler" en el
    /// título y duración de tráiler). Público estático para tests.
    /// </summary>
    public static (string Id, double DurationSeconds)? PickBestSearchResult(
        IReadOnlyList<string> printedLines, bool requireSpanish = true,
        NewsVideoKind kind = NewsVideoKind.Trailer, string? subject = null)
    {
        var subjectTokens = subject is null
            ? []
            : SignificantWords(subject).Select(Normalize).ToList();

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

            // Relevancia: el video tiene que ser DE LA OBRA, no solo "un tráiler
            // oficial en español" — mejor slideshow que la película equivocada
            if (subjectTokens.Count > 0 && !MentionsSubject(subjectTokens, $"{title} {channel}"))
                continue;

            var official = OfficialChannelRegex().IsMatch(channel)
                || title.Contains("official") || title.Contains("oficial")
                || channel.Contains("official") || title.Contains("公式") || channel.Contains("公式");

            // Requisito de idioma: sin señal de español en título o canal, afuera
            if (requireSpanish && !SpanishRegex().IsMatch(title) && !SpanishRegex().IsMatch(channel)) continue;
            // Sin el filtro de idioma, solo uploads oficiales dan confianza
            if (!requireSpanish && !official) continue;
            // Contenido de fans (reacciones/reviews/resúmenes/trailers falsos): nunca
            if (FanContentRegex().IsMatch(title)) continue;
            // Más de 6 min no es material promocional: episodio, compilado, live
            if (duration > 360) continue;

            var score = 0;
            if (KindWordRegex(kind).IsMatch(title)) score += 4;
            // El upload del distribuidor oficial gana aunque el título no diga
            // "trailer"; a igual puntaje, el orden de relevancia de YouTube
            // desempata (el primero se queda).
            if (OfficialChannelRegex().IsMatch(channel)) score += 6;
            else if (official) score += 2;
            if (duration is >= 10 and <= 300) score += 2;  // teasers arrancan en ~15s

            if (score > bestScore)
            {
                bestScore = score;
                best = (id, duration);
            }
        }

        // Sin keyword del tipo de video NI canal oficial (score < 4) no hay confianza
        return bestScore >= 4 ? best : null;
    }

    /// <summary>Palabras clave que confirman que el resultado es el tipo de video buscado.</summary>
    private static Regex KindWordRegex(NewsVideoKind kind) => kind switch
    {
        NewsVideoKind.ThemeSong => ThemeWordRegex(),
        NewsVideoKind.Short     => ShortWordRegex(),
        _                       => TrailerWordRegex(),
    };

    // ── Relevancia (el resultado tiene que mencionar la obra) ───────────────

    /// <summary>
    /// Palabras significativas de un titular/obra EN SU FORMA ORIGINAL (sirven
    /// para armar queries legibles): saca stopwords (artículos, verbos de
    /// noticia, palabras de formato tráiler/opening/…), años y tokens cortos.
    /// Público estático: el publisher lo usa para armar la query heurística.
    /// </summary>
    public static List<string> SignificantWords(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var word in WordSplitRegex().Split(text))
        {
            if (word.Length < 3) continue;
            var norm = Normalize(word);
            if (norm.Length < 3 || SubjectStopWords.Contains(norm)) continue;
            if (YearRegex().IsMatch(norm)) continue;
            if (seen.Add(norm)) result.Add(word);
        }
        return result;
    }

    /// <summary>
    /// ¿El texto (título+canal del resultado, ya normalizado por Normalize)
    /// menciona la obra? Mayoría ESTRICTA de tokens como palabra completa:
    /// "Project X" matchea 1 de 2 tokens de "Tsugumi Project" → afuera (el
    /// caso real), pero un token único ("Frieren") alcanza con aparecer.
    /// </summary>
    private static bool MentionsSubject(IReadOnlyList<string> subjectTokens, string haystack)
    {
        var normalized = Normalize(haystack);
        var matched = subjectTokens.Count(t =>
            Regex.IsMatch(normalized, $@"\b{Regex.Escape(t)}\b"));
        return matched * 2 > subjectTokens.Count;
    }

    /// <summary>Minúsculas sin diacríticos, para comparar tokens (tráiler = trailer).</summary>
    private static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    // Stopwords (formas normalizadas): artículos/preposiciones, verbos típicos
    // de titular, palabras de medio y de formato de video — lo que queda son
    // los tokens que identifican a la OBRA.
    private static readonly HashSet<string> SubjectStopWords = new(StringComparer.Ordinal)
    {
        // artículos / preposiciones / conectores (es + en)
        "los", "las", "una", "uno", "unos", "unas", "del", "con", "para", "por",
        "como", "este", "esta", "esto", "sus", "que", "mas", "sin", "the", "and",
        "with", "for", "its", "his", "her", "from", "sobre", "entre", "desde",
        "hasta", "tras",
        // verbos y muletillas de titular
        "anuncia", "anuncian", "estrena", "estrenan", "estrenara", "estreno",
        "estrenos", "revela", "revelan", "confirma", "confirman", "confirmado",
        "llega", "llegara", "llegaran", "tendra", "tendran", "lanza", "lanzan",
        "presenta", "presentan", "comparte", "compartio", "regresa", "regresan",
        "inicia", "tiene", "sera", "seran", "muestra", "celebra",
        // palabras de medio / formato de noticia
        "anime", "manga", "novela", "novelas", "serie", "series", "pelicula",
        "peliculas", "temporada", "temporadas", "parte", "live", "action",
        "adaptacion", "fecha", "nuevo", "nueva", "nuevos", "nuevas", "primer",
        "primera", "segundo", "segunda", "gran", "grupo", "staff", "elenco",
        "arte", "promocional",
        "enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto",
        "septiembre", "octubre", "noviembre", "diciembre",
        // palabras de formato de video (van en el sufijo de la query, no son obra)
        "trailer", "teaser", "avance", "oficial", "official", "espanol",
        "latino", "opening", "ending", "video", "musical", "corto", "animado",
        "cortometraje", "especial", "aniversario", "creditless", "creditos",
        "promo", "special", "movie", "song", "music", "theme",
    };

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex WordSplitRegex();

    [GeneratedRegex(@"^(19|20)\d{2}$")]
    private static partial Regex YearRegex();

    // trailer/teaser/PV/avance en varios idiomas; \b evita falsos positivos
    // (p. ej. "pvp"). 予告/特報/ティザー son los usos japoneses estándar.
    [GeneratedRegex(@"\b(trailer|tráiler|teaser|avance|pv|promo)\b|予告|特報|ティザー", RegexOptions.IgnoreCase)]
    private static partial Regex TrailerWordRegex();

    // opening/ending/MV: incluye los usos japoneses (主題歌 = theme song,
    // ノンクレジット/ノンテロップ = creditless, オープニング/エンディング,
    // OP/ED映像) y "creditless" de los uploads oficiales.
    [GeneratedRegex(@"\b(opening|ending|mv|music video|video musical|theme)\b|主題歌|ノンクレジット|ノンテロップ|オープニング|エンディング|op映像|ed映像|creditless", RegexOptions.IgnoreCase)]
    private static partial Regex ThemeWordRegex();

    // corto animado / video especial / aniversario: 特別映像 = special movie,
    // 短編 = short, 記念 = conmemorativo (aniversarios).
    [GeneratedRegex(@"\b(short|corto|cortometraje|special|especial|anniversary|aniversario)\b|特別|短編|記念", RegexOptions.IgnoreCase)]
    private static partial Regex ShortWordRegex();

    // Señal de que el video está en español: doblaje latino o subtítulos
    // incrustados ("Crunchyroll en Español", "TRÁILER OFICIAL (Doblaje latino)",
    // "en español", "subtitulado"…). "Anime Onegai" es distribuidor LATAM.
    // "spanish": YouTube traduce los nombres de canal según la región del
    // requester (visto en vivo vía WARP: "Crunchyroll in Spanish").
    // OJO: nada de palabras tipo "tráiler" acá — YouTube auto-traduce TÍTULOS
    // según región, así que un video fan en inglés puede llegar con el título
    // en español; "tráiler" traducido no dice nada del idioma del video (bug
    // real: reel publicado con un video de comentario en inglés, jul-2026).
    [GeneratedRegex(@"españ|espanol|spanish|castellano|latino|latam|doblaje|doblad|subtitulad|sub\.? esp|onegai", RegexOptions.IgnoreCase)]
    private static partial Regex SpanishRegex();

    [GeneratedRegex(@"\b(reaction|reacci[oó]n|review|rese[ñn]a|an[aá]lisis|analysis|explicado|explained|resumen|recap|amv|cosplay|theory|teor[ií]a|concept|fan[ -]?made|just dropped|breakdown|everything we know|ranked|ranking|top \d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FanContentRegex();

    // Distribuidores/estudios que suben los PV reales. La lista no necesita ser
    // exhaustiva: es solo un bonus — el título con "trailer/teaser" alcanza solo.
    [GeneratedRegex(@"aniplex|crunchyroll|toho|kadokawa|avex|toei|bandai|netflix|warner|pony canyon|king records|muse|ani-one|shueisha|kodansha|square enix|ufotable|mappa|wit studio|cloverworks|a-1 pictures|bones|kyoto animation|remow|tms", RegexOptions.IgnoreCase)]
    private static partial Regex OfficialChannelRegex();

    /// <summary>
    /// Valida que un video puntual (p. ej. el embebido en el artículo fuente)
    /// cumpla las MISMAS reglas que los resultados de búsqueda — en particular
    /// el requisito de español: kudasai suele embeber el PV japonés, y ese ya
    /// no sirve como tráiler. Devuelve el candidato (URL + duración) si pasa,
    /// null si no (→ se busca la versión latina, o slideshow). Con
    /// <paramref name="requireSpanish"/>=false y el <paramref name="kind"/>
    /// correspondiente, el mismo embebido puede rescatarse cuando el video ES
    /// la noticia (opening/corto: el idioma no aplica, solo se exige oficial).
    /// </summary>
    public async Task<TrailerCandidate?> ValidateAsync(
        string videoUrl, bool requireSpanish = true,
        NewsVideoKind kind = NewsVideoKind.Trailer, CancellationToken ct = default)
    {
        var line = await RunYtDlpPrintAsync(
            $"--skip-download --print \"%(id)s{FieldSeparator}%(duration)s{FieldSeparator}%(title)s{FieldSeparator}%(channel)s\" " +
            $"--extractor-args \"youtube:player_client={settings.YtDlpPlayerClients}\" " +
            ProxyArg() +
            $"--no-warnings --socket-timeout 20 \"{videoUrl}\"", ct);

        if (line is null) return null;

        var best = PickBestSearchResult([line], requireSpanish, kind);
        if (best is null)
        {
            logger.LogInformation(
                "Video embebido descartado como {Kind} (requireSpanish={Spanish}): {Line}",
                kind, requireSpanish, line.Length > 120 ? line[..120] : line);
            return null;
        }
        return new TrailerCandidate(videoUrl, best.Value.DurationSeconds);
    }

    /// <summary>
    /// Baja los subtítulos MANUALES en español de un video (es, es-419, es-ES…)
    /// como .vtt para quemarlos en el reel. Los subtítulos AUTOMÁTICOS
    /// (traducción por ASR) se ignoran a propósito: su calidad no da para un
    /// post publicado. Devuelve la ruta del .vtt o null; el caller limpia el
    /// directorio con <see cref="CleanUp"/>.
    /// </summary>
    public async Task<string?> DownloadSpanishSubtitlesAsync(string videoUrl, CancellationToken ct = default)
    {
        var workDir = Path.Join(Path.GetTempPath(), $"ig-subs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        var psi = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(settings.YtDlpPath) ? "yt-dlp" : settings.YtDlpPath,
            // --write-subs = SOLO subtítulos manuales (sin --write-auto-subs)
            Arguments =
                "--skip-download --write-subs --sub-langs \"es.*\" --sub-format vtt " +
                $"--extractor-args \"youtube:player_client={settings.YtDlpPlayerClients}\" " +
                ProxyArg() +
                $"--no-warnings --socket-timeout 20 -P \"{workDir}\" -o subs \"{videoUrl}\"",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");
            process.ErrorDataReceived += (_, _) => { };
            process.OutputDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* ya salió */ }
                catch (System.ComponentModel.Win32Exception) { /* best-effort */ }
                CleanUp(workDir);
                return null;
            }

            // es-419 (LATAM) primero si hay varias variantes
            var subs = Directory.GetFiles(workDir, "*.vtt")
                .OrderByDescending(f => f.Contains("es-419") ? 1 : 0)
                .FirstOrDefault();
            if (subs is null)
            {
                CleanUp(workDir);
                return null;
            }

            logger.LogInformation("Subtítulos en español encontrados para {Url}: {File}",
                videoUrl, Path.GetFileName(subs));
            return subs;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Descarga de subtítulos falló para {Url}", videoUrl);
            CleanUp(workDir);
            return null;
        }
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
