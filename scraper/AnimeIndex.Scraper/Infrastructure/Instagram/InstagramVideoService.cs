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
/// Un reel renderizado y su duración EXACTA en segundos.
///
/// La duración viaja con el MP4 porque no se puede recuperar después: la API de
/// insights no la expone, y derivarla de las métricas no da (avg_watch_time es
/// un promedio, no la duración). Pero acá la sabemos con precisión — es la que
/// le pasamos a ffmpeg. Sin este dato solo se puede mirar watch time absoluto,
/// que está ACOTADO por la duración: por eso "los reels con tráiler retienen
/// más" y "el watch time predice las views" parecían dos hallazgos distintos
/// cuando en buena medida eran el mismo (ver §2.C del playbook).
/// </summary>
public sealed record RenderedReel(byte[] Mp4, double DurationSeconds);

/// <summary>
/// Generates a 9:16 "motion card" MP4 (1080×1920) from a still card image:
/// zoom lento estilo Ken Burns, pista de audio AAC silenciosa. Nada de fade
/// desde negro al arranque — el frame 0 va a brillo completo.
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

    // ── Slideshow (reel multi-slide) ──
    // Cada slide se ve ~4s con crossfade de 0.6s entre escenas → 5 slides ≈ 17.6s.
    private const double SlideSeconds = 4.0;
    private const double CrossfadeSeconds = 0.6;
    // Zoom por slide más sutil que el single-card: alterna in/out entre escenas.
    private const double SlideMaxZoom = 1.08;

    /// <summary>
    /// Renders the motion-card MP4 (1080×1920) and returns the bytes.
    /// Con <paramref name="overlayPng"/> anima en capas: Ken Burns en el fondo
    /// y el bloque de texto entrando con slide-up + fade (easing cúbico) —
    /// motion graphics de verdad, texto siempre nítido. Sin overlay, zoom plano
    /// sobre la tarjeta completa. Con <paramref name="musicMp3"/> mezcla el
    /// track (fade in/out + loudnorm); sin música, pista AAC silenciosa.
    /// </summary>
    public async Task<RenderedReel> GenerateMotionCardAsync(
        byte[] cardImageBytes,
        byte[]? overlayPng = null,
        byte[]? musicMp3 = null,
        int musicStartSeconds = 0,
        CancellationToken ct = default)
    {
        // Path.Join en vez de Path.Combine: los segmentos son siempre relativos
        // (nombre de archivo fijo / Guid) pero Combine resetea silenciosamente
        // si CodeQL no puede probarlo — Join concatena sin ese riesgo.
        var workDir = Path.Join(Path.GetTempPath(), $"ig-reel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var inputPath = Path.Join(workDir, "card.png");
        var outputPath = Path.Join(workDir, "reel.mp4");
        string? overlayPath = null;
        string? musicPath = null;

        try
        {
            await File.WriteAllBytesAsync(inputPath, cardImageBytes, ct);
            if (overlayPng is not null)
            {
                overlayPath = Path.Join(workDir, "overlay.png");
                await File.WriteAllBytesAsync(overlayPath, overlayPng, ct);
            }
            if (musicMp3 is not null)
            {
                musicPath = Path.Join(workDir, "music.mp3");
                await File.WriteAllBytesAsync(musicPath, musicMp3, ct);
            }

            var args = BuildFfmpegArguments(
                inputPath, outputPath, settings.ReelDurationSeconds, musicPath, musicStartSeconds, overlayPath);
            await RunFfmpegAsync(args, ct);

            var bytes = await File.ReadAllBytesAsync(outputPath, ct);
            logger.LogInformation("Generated motion-card reel: {Seconds}s, {Mb:F1} MB",
                settings.ReelDurationSeconds, bytes.Length / 1024.0 / 1024.0);
            return new RenderedReel(bytes, settings.ReelDurationSeconds);
        }
        finally
        {
            // Limpieza best-effort del temp dir — solo los fallos esperables de
            // I/O quedan silenciados; cualquier otra cosa (p. ej. un bug) se ve.
            try { Directory.Delete(workDir, recursive: true); }
            catch (IOException) { /* archivo en uso — se limpia solo con el temp cleaner del runner */ }
            catch (UnauthorizedAccessException) { /* permisos — mismo caso, best-effort */ }
        }
    }

    // ── Reel de tráiler ──
    // Las slides informativas de después duran 3.5s cada una; el audio ORIGINAL
    // del tráiler suena de fondo durante todo el reel con fade-out al cierre.
    private const double InfoSlideSeconds = 3.5;
    // Fade-out corto: 1.8s era un disolve largo que arrastraba el final del reel.
    private const double TrailerAudioFadeOut = 0.9;

    // Cuánto tráiler saltear al arranque (ver TrailerStartSkipFor).
    private const double MinStartSkip = 1.5;
    private const double MaxStartSkip = 6.0;
    private const double StartSkipFraction = 0.12;

    // ── Composición full-bleed ──
    // El propio tráiler, desenfocado y ampliado, hace de fondo; la banda nítida
    // va centrada encima. Antes el fondo era un panel abismo fijo y la banda
    // ocupaba 900 de 1920 px — el 47 % de la pantalla flotando sobre un fondo
    // muerto, que se lee como "una tarjeta con un video adentro" en vez de como
    // un video.
    //
    // El desenfoque se calcula en CHICO y se amplía: gblur con sigma grande
    // sobre 1080×1920 a 30 fps es carísimo, y sobre 270×480 es ~16× más barato
    // con un resultado indistinguible una vez ampliado.
    private const int BlurWidth = 270;
    private const int BlurHeight = 480;
    private const int BlurSigma = 10;
    // Alto máximo de la banda nítida. 1250 deja pasar casi entero un clip
    // vertical (los respaldos de X vienen así) sin recortarle medio cuadro.
    private const int BandMaxHeight = 1250;
    // Centro de la banda, como fracción de la altura. Justo en el medio: con
    // 0.46 el borde superior de la banda caía en y≈579 y la segunda línea del
    // gancho terminaba en ≈583 — pegada al filo del video (verificado
    // renderizando el reel completo con ffmpeg, no solo los argumentos).
    private const double BandCenter = 0.50;

    /// <summary>
    /// Cuánto tráiler saltear al arranque. Era la constante 1,5 s, pero los PV
    /// oficiales abren con logos de distribuidora y estudio que duran 3-6 s: en
    /// un tráiler de 90 s eso significaba regalarle a un logo el primer segundo
    /// y medio del reel, que es justo donde se decide el skip. Proporcional a la
    /// duración, con piso y techo para no saltearse el contenido de un teaser
    /// corto ni quedarse corto en uno largo. Duración desconocida (0) → el piso.
    /// Público estático para tests.
    /// </summary>
    public static double TrailerStartSkipFor(double durationSeconds) =>
        durationSeconds <= 0
            ? MinStartSkip
            : Math.Round(Math.Clamp(durationSeconds * StartSkipFraction, MinStartSkip, MaxStartSkip), 1);

    /// <summary>
    /// Renders el Reel "tráiler + titular": el PV a pantalla completa —banda
    /// nítida sobre una copia desenfocada de sí mismo— CON SU AUDIO ORIGINAL (el
    /// sonido del tráiler es lo que la gente quiere escuchar), el gancho desde el
    /// frame 0, el bloque editorial entrando con el slide-up de marca, y a
    /// continuación las slides informativas con el audio del tráiler siguiendo de
    /// fondo. El formato de las cuentas grandes de noticias de anime.
    /// </summary>
    public async Task<RenderedReel> GenerateTrailerReelAsync(
        string trailerPath,
        byte[] hookPng,
        byte[] overlayPng,
        IReadOnlyList<byte[]> infoSlides,
        double trailerDurationSeconds,
        string? subtitlesPath = null,
        CancellationToken ct = default)
    {
        var workDir = Path.Join(Path.GetTempPath(), $"ig-reel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var hookPath = Path.Join(workDir, "hook.png");
        var overlayPath = Path.Join(workDir, "overlay.png");
        var outputPath = Path.Join(workDir, "reel.mp4");

        try
        {
            await File.WriteAllBytesAsync(hookPath, hookPng, ct);
            await File.WriteAllBytesAsync(overlayPath, overlayPng, ct);
            var slidePaths = new List<string>(infoSlides.Count);
            for (var i = 0; i < infoSlides.Count; i++)
            {
                var p = Path.Join(workDir, $"info-{i}.jpg");
                await File.WriteAllBytesAsync(p, infoSlides[i], ct);
                slidePaths.Add(p);
            }

            // El tráiler entra pasado el arranque → los subtítulos se re-timean
            // para que no queden corridos respecto del video.
            var startSkip = TrailerStartSkipFor(trailerDurationSeconds);
            string? subsWorkPath = null;
            if (subtitlesPath is not null)
            {
                var shifted = ShiftVttTimestamps(
                    await File.ReadAllTextAsync(subtitlesPath, ct), -startSkip);
                subsWorkPath = Path.Join(workDir, "subs.vtt");
                await File.WriteAllTextAsync(subsWorkPath, shifted, ct);
            }

            // Cuánto tráiler mostrar: lo disponible tras saltear la intro, capado
            // por settings y por el techo duro del reel.
            //
            // 120 s de techo: la idea es que el tráiler se vea ENTERO (ver
            // TrailerClipSeconds — cortarlo le pone un techo al watch time, que es
            // lo único que correlaciona con views). Sigue habiendo un tope porque
            // PickBestSearchResult acepta videos de hasta 6 min y un "tráiler" de
            // 6 minutos es un compilado, no un PV.
            var maxForBudget = 120.0 - slidePaths.Count * InfoSlideSeconds;
            var available = trailerDurationSeconds > startSkip + 4
                ? trailerDurationSeconds - startSkip
                : settings.TrailerClipSeconds;   // duración desconocida → usar el cap
            var trailerSeconds = Math.Round(
                Math.Min(Math.Min(available, settings.TrailerClipSeconds), maxForBudget), 1);

            var args = BuildTrailerReelArguments(
                trailerPath, hookPath, overlayPath, slidePaths, outputPath, trailerSeconds,
                startSkip, subsWorkPath);
            await RunFfmpegAsync(args, ct);

            var bytes = await File.ReadAllBytesAsync(outputPath, ct);
            var totalSeconds = trailerSeconds + slidePaths.Count * InfoSlideSeconds;
            logger.LogInformation(
                "Generated trailer reel: {Trailer}s de tráiler (skip {Skip}s) + {Slides} slides " +
                "= {Total}s, {Mb:F1} MB",
                trailerSeconds, startSkip, slidePaths.Count, totalSeconds, bytes.Length / 1024.0 / 1024.0);
            return new RenderedReel(bytes, totalSeconds);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch (IOException) { /* best-effort */ }
            catch (UnauthorizedAccessException) { /* best-effort */ }
        }
    }

    /// <summary>
    /// Público + static para poder testear sin ffmpeg. Inputs: 0 = tráiler
    /// (video + SU AUDIO — nunca música nuestra), 1 = capa del gancho, 2 =
    /// overlay editorial, 3.. = slides informativas.
    ///
    /// El tráiler se usa DOS veces: una copia desenfocada y ampliada llena los
    /// 1080×1920 de fondo, y la banda nítida va centrada encima. Sobre eso, el
    /// gancho desde el frame 0 (sin fade) y el bloque editorial con el slide-up
    /// de marca. Después se concatenan las slides (Ken Burns suave) mientras el
    /// audio del tráiler sigue sonando, con un fade-out corto al cierre.
    /// </summary>
    public static string BuildTrailerReelArguments(
        string trailerPath, string hookPath, string overlayPath,
        IReadOnlyList<string> infoSlidePaths, string outputPath, double trailerSeconds,
        double startSkip = MinStartSkip, string? subtitlesPath = null)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var totalSeconds = trailerSeconds + infoSlidePaths.Count * InfoSlideSeconds;
        var trailArg = trailerSeconds.ToString("0.0#", inv);
        var totalArg = totalSeconds.ToString("0.0#", inv);
        var band = BandMaxHeight.ToString(inv);

        var inputs = new List<string>
        {
            // -ss antes de -i: seek rápido; trae video Y audio del tráiler
            $"-ss {startSkip.ToString("0.0#", inv)} -i \"{trailerPath}\"",
            $"-loop 1 -framerate {Fps} -t {trailArg} -i \"{hookPath}\"",
            $"-loop 1 -framerate {Fps} -t {trailArg} -i \"{overlayPath}\"",
        };
        // Slides: input de UN frame (sin -loop) + zoompan de d frames — mismo
        // patrón del slideshow (con -loop+d se multiplican los frames).
        foreach (var p in infoSlidePaths)
            inputs.Add($"-i \"{p}\"");

        // Subtítulos MANUALES en español quemados sobre la banda del tráiler
        // (cuando el tráiler no está en español pero tiene subs oficiales).
        // Se aplican DESPUÉS del scale/crop → se renderizan a 1080 de ancho.
        var subsFilter = subtitlesPath is null
            ? ""
            : $"subtitles='{EscapeForFilter(subtitlesPath)}'" +
              ":force_style='FontName=Arial,FontSize=16,Bold=1,Outline=2,MarginV=30',";

        var filters = new List<string>
        {
            // Una sola decodificación del tráiler, dos usos
            $"[0:v]fps={Fps},split=2[src][blurbase]",
            // Fondo: el propio tráiler desenfocado y ampliado a pantalla completa.
            // El blur se hace en 270×480 y se escala — mismo resultado visible que
            // gblur a 1080×1920, ~16× más barato en el runner.
            $"[blurbase]scale={BlurWidth}:{BlurHeight}:force_original_aspect_ratio=increase,setsar=1," +
            $"crop={BlurWidth}:{BlurHeight},gblur=sigma={BlurSigma}," +
            $"scale={OutWidth}:{OutHeight}:flags=bicubic," +
            $"tpad=stop_mode=clone:stop_duration={trailArg}[bg]",
            // Banda nítida: 1080 de ancho, capada por si el clip es vertical,
            // congelada al final si quedó corto
            $"[src]scale={OutWidth}:-2,setsar=1," +
            $"crop={OutWidth}:'min(ih,{band})':0:'(ih-min(ih,{band}))/2'," +
            subsFilter +
            $"tpad=stop_mode=clone:stop_duration={trailArg}[band]",
            $"[bg][band]overlay=x='(W-w)/2':y='H*{BandCenter.ToString("0.00", inv)}-h/2'[base]",
            // El gancho está desde el FRAME 0 y sin fade: es lo único que puede
            // frenar el scroll antes de que se decida el skip (mediana 55 %).
            "[1:v]format=rgba[hk]",
            "[base][hk]overlay=x=0:y=0[hooked]",
            // El bloque editorial sí entra animado — es la firma de marca, no el
            // gancho. SIN fade desde negro sobre el video: el `fade=t=in:st=0:d=0.4`
            // que había acá arrancaba el reel en negro justo en el peor momento.
            // De paso arregla la miniatura: IG toma el primer frame cuando el
            // cover_url no sube.
            "[2:v]format=rgba,fade=t=in:st=0.5:d=0.8:alpha=1[ov]",
            "[hooked][ov]overlay=x=0:y='pow(1-min(1,max(0,(t-0.5)/0.9)),3)*80'," +
            $"trim=duration={trailArg},setpts=PTS-STARTPTS[seg0]",
        };

        // Cada slide informativa: Ken Burns suave y duración fija
        var slideFrames = (int)(InfoSlideSeconds * Fps);
        var zoomStep = ((SlideMaxZoom - 1.0) / slideFrames).ToString("0.00000000", inv);
        for (var i = 0; i < infoSlidePaths.Count; i++)
        {
            filters.Add(
                $"[{i + 3}:v]scale={OutWidth * 2}:{OutHeight * 2}:flags=lanczos," +
                $"zoompan=z='min(1+on*{zoomStep},{SlideMaxZoom.ToString("0.00", inv)})':d={slideFrames}" +
                $":x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':s={OutWidth}x{OutHeight}:fps={Fps}[info{i}]");
        }

        // Tráiler + slides en una sola línea de tiempo
        var segments = "[seg0]" + string.Concat(
            Enumerable.Range(0, infoSlidePaths.Count).Select(i => $"[info{i}]"));
        filters.Add($"{segments}concat=n={infoSlidePaths.Count + 1}:v=1:a=0,format=yuv420p[v]");

        // Audio: el ORIGINAL del tráiler, sonando también de fondo durante las
        // slides; apad cubre tráilers más cortos que el reel; fade-out al cierre.
        var fadeOutStart = Math.Max(0, totalSeconds - TrailerAudioFadeOut).ToString("0.0#", inv);
        filters.Add(
            $"[0:a]apad,atrim=0:{totalArg}," +
            $"afade=t=out:st={fadeOutStart}:d={TrailerAudioFadeOut.ToString("0.0#", inv)}," +
            "loudnorm=I=-16:TP=-1.5:LRA=11[a]");

        return string.Join(' ',
            "-y",
            string.Join(' ', inputs),
            $"-filter_complex \"{string.Join(';', filters)}\"",
            "-map [v] -map [a]",
            $"-t {totalArg} -r {Fps}",
            // preset "fast" y no "medium": desde que el tráiler va entero (hasta 90 s) el
            // render creció ~3×, y a 6 Mbps sobre 1080×1920 la diferencia de calidad
            // entre los dos presets es imperceptible mientras que la de tiempo no lo es.
            "-c:v libx264 -profile:v high -preset fast -flags +cgop -g 60 -sc_threshold 0",
            "-b:v 6M -maxrate 8M -bufsize 12M",
            "-c:a aac -b:a 128k -ar 44100",
            "-movflags +faststart",
            $"\"{outputPath}\"");
    }

    /// <summary>
    /// Renders a multi-slide Reel (slideshow): cada slide 9:16 con Ken Burns
    /// alternado (in/out) y crossfade entre escenas, más música/pista muda.
    /// Con 1 sola slide degrada al motion-card simple.
    /// </summary>
    public async Task<RenderedReel> GenerateSlideshowAsync(
        IReadOnlyList<byte[]> slides,
        byte[]? musicMp3 = null,
        int musicStartSeconds = 0,
        CancellationToken ct = default)
    {
        if (slides.Count == 0) throw new ArgumentException("Slideshow requiere al menos 1 slide", nameof(slides));
        if (slides.Count == 1)
            return await GenerateMotionCardAsync(slides[0], null, musicMp3, musicStartSeconds, ct);

        var workDir = Path.Join(Path.GetTempPath(), $"ig-reel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var outputPath = Path.Join(workDir, "reel.mp4");
        string? musicPath = null;

        try
        {
            var slidePaths = new List<string>(slides.Count);
            for (var i = 0; i < slides.Count; i++)
            {
                var p = Path.Join(workDir, $"slide-{i}.jpg");
                await File.WriteAllBytesAsync(p, slides[i], ct);
                slidePaths.Add(p);
            }
            if (musicMp3 is not null)
            {
                musicPath = Path.Join(workDir, "music.mp3");
                await File.WriteAllBytesAsync(musicPath, musicMp3, ct);
            }

            var args = BuildSlideshowArguments(slidePaths, outputPath, musicPath, musicStartSeconds);
            await RunFfmpegAsync(args, ct);

            var bytes = await File.ReadAllBytesAsync(outputPath, ct);
            var totalSeconds = SlideshowSeconds(slides.Count);
            logger.LogInformation("Generated slideshow reel: {Slides} slides = {Total}s, {Mb:F1} MB",
                slides.Count, totalSeconds, bytes.Length / 1024.0 / 1024.0);
            return new RenderedReel(bytes, totalSeconds);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch (IOException) { /* best-effort */ }
            catch (UnauthorizedAccessException) { /* best-effort */ }
        }
    }

    /// <summary>
    /// Duración total de un slideshow de <paramref name="slideCount"/> escenas:
    /// cada una dura S y se solapa F con la siguiente. Público estático para
    /// tests y para que el publisher la guarde sin re-derivarla.
    ///
    /// Solo aplica con 2 o más escenas: con una sola,
    /// <see cref="GenerateSlideshowAsync"/> delega en el motion-card, que dura
    /// <c>ReelDurationSeconds</c> y devuelve su propia duración.
    /// </summary>
    public static double SlideshowSeconds(int slideCount) =>
        Math.Round(slideCount * SlideSeconds - Math.Max(0, slideCount - 1) * CrossfadeSeconds, 1);

    /// <summary>
    /// Público + static para poder testear sin ffmpeg. Inputs: 0..n-1 = slides
    /// (loopeadas), n = audio. Cada slide lleva zoompan (zoom-in en pares,
    /// zoom-out en impares) y las escenas se unen con xfade: la escena k arranca
    /// en offset k·(S−F). Duración total = n·S − (n−1)·F.
    /// </summary>
    public static string BuildSlideshowArguments(
        IReadOnlyList<string> slidePaths, string outputPath,
        string? musicPath = null, int musicStartSeconds = 0)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var n = slidePaths.Count;
        var totalSeconds = n * SlideSeconds - (n - 1) * CrossfadeSeconds;
        var slideFrames = (int)(SlideSeconds * Fps);
        var zoomStep = (SlideMaxZoom - 1.0) / slideFrames;

        var inputs = new List<string>();
        var filters = new List<string>();

        // zoompan sobre imagen fija: input de UN frame (sin -loop) y d = frames
        // del clip — mismo patrón que el single-card. +6 frames (0.2s) de colchón
        // porque xfade exige que cada clip cubra offset+duración exactos.
        var clipFrames = slideFrames + 6;

        for (var i = 0; i < n; i++)
        {
            inputs.Add($"-i \"{slidePaths[i]}\"");

            // Zoom alternado para que el slideshow "respire": in, out, in, out…
            var zoomExpr = i % 2 == 0
                ? $"min(1+on*{zoomStep.ToString("0.00000000", inv)},{SlideMaxZoom.ToString("0.00", inv)})"
                : $"max({SlideMaxZoom.ToString("0.00", inv)}-on*{zoomStep.ToString("0.00000000", inv)},1.0)";

            filters.Add(
                $"[{i}:v]scale={OutWidth * 2}:{OutHeight * 2}:flags=lanczos," +
                $"zoompan=z='{zoomExpr}':d={clipFrames}" +
                $":x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':s={OutWidth}x{OutHeight}:fps={Fps}[v{i}]");
        }

        // Cadena de crossfades: [v0][v1]→[x1], [x1][v2]→[x2], …
        var prev = "[v0]";
        for (var k = 1; k < n; k++)
        {
            var offset = (k * (SlideSeconds - CrossfadeSeconds)).ToString("0.0#", inv);
            var label = k == n - 1 ? "[vx]" : $"[x{k}]";
            filters.Add($"{prev}[v{k}]xfade=transition=fade:duration={CrossfadeSeconds.ToString("0.0#", inv)}:offset={offset}{label}");
            prev = label;
        }
        filters.Add("[vx]format=yuv420p[v]");

        var audioIdx = n;
        var durArg = totalSeconds.ToString("0.0#", inv);
        string audioInput, audioFilter, audioMap;
        if (musicPath is not null)
        {
            var fadeOutStart = Math.Max(0, totalSeconds - 1.5).ToString("0.0#", inv);
            audioInput = $"-ss {musicStartSeconds} -t {durArg} -i \"{musicPath}\"";
            audioFilter = $";[{audioIdx}:a]afade=t=in:st=0:d=0.8,afade=t=out:st={fadeOutStart}:d=1.5," +
                          "loudnorm=I=-16:TP=-1.5:LRA=11[a]";
            audioMap = "-map [a]";
        }
        else
        {
            audioInput = $"-f lavfi -t {durArg} -i anullsrc=channel_layout=stereo:sample_rate=44100";
            audioFilter = "";
            audioMap = $"-map {audioIdx}:a";
        }

        return string.Join(' ',
            "-y",
            string.Join(' ', inputs),
            audioInput,
            $"-filter_complex \"{string.Join(';', filters)}{audioFilter}\"",
            $"-map [v] {audioMap}",
            $"-t {durArg} -r {Fps}",
            // preset "fast" y no "medium": desde que el tráiler va entero (hasta 90 s) el
            // render creció ~3×, y a 6 Mbps sobre 1080×1920 la diferencia de calidad
            // entre los dos presets es imperceptible mientras que la de tiempo no lo es.
            "-c:v libx264 -profile:v high -preset fast -flags +cgop -g 60 -sc_threshold 0",
            "-b:v 6M -maxrate 8M -bufsize 12M",
            "-c:a aac -b:a 128k -ar 44100",
            "-movflags +faststart",
            "-shortest",
            $"\"{outputPath}\"");
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
        // filtro recorta la ventana 1080×1920. SIN fade desde negro: el frame 0
        // tiene que ser la tarjeta a brillo completo (ver [seg0] del reel de
        // tráiler — mismo motivo, el skip se decide en el primer instante).
        var backgroundFilter =
            $"[0:v]scale={OutWidth * 2}:{OutHeight * 2}:flags=lanczos," +
            $"zoompan=z='min(1+on*{zoomStep.ToString("0.00000000", inv)},{MaxZoom.ToString("0.00", inv)})'" +
            $":d={frames}:x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':s={OutWidth}x{OutHeight}:fps={Fps}";

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
            // preset "fast" y no "medium": desde que el tráiler va entero (hasta 90 s) el
            // render creció ~3×, y a 6 Mbps sobre 1080×1920 la diferencia de calidad
            // entre los dos presets es imperceptible mientras que la de tiempo no lo es.
            "-c:v libx264 -profile:v high -preset fast -flags +cgop -g 60 -sc_threshold 0",
            "-b:v 6M -maxrate 8M -bufsize 12M",
            "-c:a aac -b:a 128k -ar 44100",
            "-movflags +faststart",
            "-shortest",
            $"\"{outputPath}\"");
    }

    /// <summary>
    /// Corre las marcas de tiempo de un WebVTT en <paramref name="shiftSeconds"/>
    /// (negativo = adelantar). Necesario porque el tráiler entra al reel desde
    /// el segundo 1.5 y los subs vienen timeados contra el video original.
    /// Tiempos que caen antes de cero se clampean a 00:00:00.000. Público
    /// estático para tests.
    /// </summary>
    public static string ShiftVttTimestamps(string vttContent, double shiftSeconds)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            vttContent,
            @"(?:(\d{2,}):)?(\d{2}):(\d{2})\.(\d{3})",
            m =>
            {
                var hours = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
                var t = new TimeSpan(0, hours, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                        int.Parse(m.Groups[4].Value))
                    + TimeSpan.FromSeconds(shiftSeconds);
                if (t < TimeSpan.Zero) t = TimeSpan.Zero;
                return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
            });
    }

    /// <summary>
    /// Escapa una ruta para usarla dentro de un filtro de ffmpeg (subtitles=…):
    /// separadores a "/" y ":" escapado (en Windows "C:" rompería el parseo).
    /// </summary>
    private static string EscapeForFilter(string path) =>
        path.Replace('\\', '/').Replace(":", "\\:");

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
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* ya salió solo */ }
                catch (System.ComponentModel.Win32Exception) { /* falló el kill nativo — best-effort */ }
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
