using AnimeIndex.Scraper.Infrastructure;
using AnimeIndex.Scraper.Infrastructure.Instagram;

namespace AnimeIndex.Api.Tests;

public class InstagramVideoServiceTests
{
    [Fact]
    public void BuildFfmpegArguments_MeetsReelsSpecs()
    {
        var args = InstagramVideoService.BuildFfmpegArguments("in.png", "out.mp4", 12);

        // moov atom al frente — requisito explícito de la Graph API para Reels
        Assert.Contains("-movflags +faststart", args);
        // H.264 + yuv420p + closed GOP, como piden las specs
        Assert.Contains("-c:v libx264", args);
        Assert.Contains("format=yuv420p", args);
        Assert.Contains("+cgop", args);
        // Pista de audio silenciosa AAC (48 kHz máximo según specs — usamos 44.1)
        Assert.Contains("anullsrc", args);
        Assert.Contains("-c:a aac", args);
        // Salida 1080×1920 (9:16) a 30 fps
        Assert.Contains("s=1080x1920", args);
        Assert.Contains("fps=30", args);
    }

    [Fact]
    public void BuildFfmpegArguments_DurationDrivesFrameCountAndTimestamps()
    {
        var args = InstagramVideoService.BuildFfmpegArguments("in.png", "out.mp4", 15);

        // 15 s × 30 fps = 450 frames para zoompan, y -t 15 en input y salida
        Assert.Contains("d=450", args);
        Assert.Contains("-t 15", args);
        // El paso de zoom usa punto decimal aunque la culture local use coma
        Assert.DoesNotContain("0,000", args);
    }

    [Fact]
    public void BuildFfmpegArguments_QuotesPathsWithSpaces()
    {
        var args = InstagramVideoService.BuildFfmpegArguments(
            @"C:\temp dir\card.png", @"C:\temp dir\reel.mp4", 12);

        Assert.Contains("\"C:\\temp dir\\card.png\"", args);
        Assert.Contains("\"C:\\temp dir\\reel.mp4\"", args);
    }

    [Fact]
    public void BuildFfmpegArguments_WithMusic_MixesTrackInsteadOfSilence()
    {
        var args = InstagramVideoService.BuildFfmpegArguments(
            "in.png", "out.mp4", 12, musicPath: "music.mp3", musicStartSeconds: 5);

        // El track reemplaza a la pista silenciosa
        Assert.DoesNotContain("anullsrc", args);
        Assert.Contains("-ss 5 -t 12 -i \"music.mp3\"", args);
        // Fade-in, fade-out al cierre (12 - 1.5 = 10.5) y nivel social estándar
        Assert.Contains("afade=t=in:st=0:d=0.8", args);
        Assert.Contains("afade=t=out:st=10.5:d=1.5", args);
        Assert.Contains("loudnorm=I=-16", args);
        Assert.Contains("-map [a]", args);
        // Sin overlay, el audio es el input 1
        Assert.Contains("[1:a]afade", args);
    }

    [Fact]
    public void BuildFfmpegArguments_WithOverlay_AnimatesTextLayer()
    {
        var args = InstagramVideoService.BuildFfmpegArguments(
            "bg.jpg", "out.mp4", 12, overlayPath: "overlay.png");

        // El overlay entra como input loopeado con alpha
        Assert.Contains("-loop 1 -i \"overlay.png\"", args);
        Assert.Contains("format=rgba", args);
        // Fade del texto arrancando a los 0.5s, sobre el canal alpha
        Assert.Contains("fade=t=in:st=0.5:d=0.8:alpha=1", args);
        // Slide-up con easing cúbico (80px → 0)
        Assert.Contains("overlay=x=0:y='pow(1-min(1,max(0,(t-0.5)/0.9)),3)*80'", args);
        // Con overlay, el audio (silencioso acá) pasa a ser el input 2
        Assert.Contains("-map 2:a", args);
    }

    [Fact]
    public void BuildFfmpegArguments_WithOverlayAndMusic_AudioIndexShifts()
    {
        var args = InstagramVideoService.BuildFfmpegArguments(
            "bg.jpg", "out.mp4", 12, musicPath: "music.mp3", overlayPath: "overlay.png");

        // fondo=0, overlay=1, música=2
        Assert.Contains("[2:a]afade", args);
        Assert.Contains("-map [a]", args);
        Assert.DoesNotContain("anullsrc", args);
    }

    [Fact]
    public void BuildSlideshowArguments_ChainsCrossfadesWithCorrectOffsets()
    {
        var args = InstagramVideoService.BuildSlideshowArguments(
            ["s0.jpg", "s1.jpg", "s2.jpg"], "out.mp4");

        // Escena k arranca en k·(4−0.6): 3.4 y 6.8; duración total 3·4 − 2·0.6 = 10.8
        Assert.Contains("xfade=transition=fade:duration=0.6:offset=3.4", args);
        Assert.Contains("offset=6.8[vx]", args);
        Assert.Contains("-t 10.8", args);
        // Zoom alternado: slide 0 acerca, slide 1 aleja
        Assert.Contains("[0:v]", args);
        Assert.Contains("min(1+on*", args);
        Assert.Contains("max(1.08-on*", args);
        // Sin música → pista silenciosa como input 3 (después de las 3 slides)
        Assert.Contains("anullsrc", args);
        Assert.Contains("-map 3:a", args);
        // Specs de Reels intactas
        Assert.Contains("-movflags +faststart", args);
        Assert.Contains("format=yuv420p", args);
    }

    [Fact]
    public void BuildSlideshowArguments_WithMusic_UsesTrackAtCorrectIndex()
    {
        var args = InstagramVideoService.BuildSlideshowArguments(
            ["s0.jpg", "s1.jpg"], "out.mp4", musicPath: "music.mp3", musicStartSeconds: 5);

        Assert.DoesNotContain("anullsrc", args);
        // 2 slides → música es el input 2; total 2·4 − 0.6 = 7.4
        Assert.Contains("[2:a]afade", args);
        Assert.Contains("-ss 5 -t 7.4 -i \"music.mp3\"", args);
        // Fade-out arranca en 7.4 − 1.5 = 5.9
        Assert.Contains("afade=t=out:st=5.9", args);
    }

    [Fact]
    public void BuildTrailerReelArguments_UsesOriginalTrailerAudio()
    {
        var args = InstagramVideoService.BuildTrailerReelArguments(
            "trailer.mp4", "hook.png", "overlay.png", [], "out.mp4", 40);

        // El tráiler entra salteando el arranque (logos/negro) CON su audio
        // original — nada de música nuestra ni pista silenciosa
        Assert.Contains("-ss 1.5 -i \"trailer.mp4\"", args);
        Assert.Contains("[0:a]apad,atrim=0:40", args);
        Assert.Contains("-map [v] -map [a]", args);
        Assert.DoesNotContain("anullsrc", args);
        Assert.DoesNotContain("music", args);
        // Fade-out corto del audio al cierre (40 − 0.9 = 39.1) y nivel estándar
        Assert.Contains("afade=t=out:st=39.1", args);
        Assert.Contains("loudnorm=I=-16", args);
        // El clip entra ENTERO en la caja fija (sin recortes) y se congela si
        // quedó corto. La caja no depende del aspecto de la fuente.
        Assert.Contains("scale=1080:600:force_original_aspect_ratio=decrease", args);
        Assert.Contains("tpad=stop_mode=clone", args);
        // Y se ancla a una Y FIJA: el video ocupa 610-1210 y nada se le escribe
        // encima — el pie arranca recién en 1240 (ver DrawVideoReelFooter).
        Assert.Contains("overlay=x='(W-w)/2':y=610", args);
        Assert.DoesNotContain("crop=1080:'min(ih", args);
        Assert.Contains("fade=t=in:st=0.5:d=0.8:alpha=1", args);
        // …pero NADA de fade desde negro sobre el video (ver test dedicado)
        Assert.DoesNotContain("fade=t=in:st=0:d=0.4", args);
        // Specs de Reels intactas
        Assert.Contains("-movflags +faststart", args);
        Assert.Contains("format=yuv420p", args);
        Assert.Contains("-t 40", args);
    }

    [Fact]
    public void BuildTrailerReelArguments_BurnsSpanishSubtitlesWhenProvided()
    {
        var args = InstagramVideoService.BuildTrailerReelArguments(
            "t.mp4", "hook.png", "ov.png", [], "out.mp4", 30,
            subtitlesPath: @"C:\temp\subs.vtt");

        // El filtro subtitles va sobre la banda del tráiler, con la ruta
        // escapada para el parser de filtros (C: rompería sin escapar)
        Assert.Contains(@"subtitles='C\:/temp/subs.vtt'", args);
        Assert.Contains("force_style=", args);

        // Sin subs no hay filtro
        var noSubs = InstagramVideoService.BuildTrailerReelArguments(
            "t.mp4", "hook.png", "ov.png", [], "out.mp4", 30);
        Assert.DoesNotContain("subtitles=", noSubs);
    }

    [Theory]
    // El tráiler entra al reel desde el segundo 1.5 → los subs se adelantan 1.5s
    [InlineData("00:00:02.000 --> 00:00:04.500", -1.5, "00:00:00.500 --> 00:00:03.000")]
    // Tiempos que caerían en negativo se clampean a cero
    [InlineData("00:00:01.000 --> 00:00:02.000", -1.5, "00:00:00.000 --> 00:00:00.500")]
    // Formato con horas se preserva
    [InlineData("01:02:03.250 --> 01:02:05.750", -1.5, "01:02:01.750 --> 01:02:04.250")]
    public void ShiftVttTimestamps_ShiftsAndClamps(string cue, double shift, string expected)
        => Assert.Equal(expected, InstagramVideoService.ShiftVttTimestamps(cue, shift));

    [Fact]
    public void BuildTrailerReelArguments_AppendsInfoSlidesAfterTrailer()
    {
        var args = InstagramVideoService.BuildTrailerReelArguments(
            "t.mp4", "hook.png", "ov.png", ["kp1.jpg", "cta.jpg"], "out.mp4", 30);

        // Las 2 slides entran como inputs 3 y 4 y se concatenan tras el tráiler
        Assert.Contains("-i \"kp1.jpg\"", args);
        Assert.Contains("-i \"cta.jpg\"", args);
        Assert.Contains("[3:v]", args);
        Assert.Contains("[4:v]", args);
        Assert.Contains("[seg0][info0][info1]concat=n=3:v=1:a=0", args);
        // El segmento del tráiler se recorta a sus 30s antes del concat
        Assert.Contains("trim=duration=30", args);
        // Total = 30 + 2×3.5 = 37s; el audio del tráiler cubre TODO el reel
        Assert.Contains("-t 37", args);
        Assert.Contains("[0:a]apad,atrim=0:37", args);
        // Fade-out del audio al final de las slides (37 − 0.9 = 36.1)
        Assert.Contains("afade=t=out:st=36.1", args);
    }
}

/// <summary>
/// El primer frame decide el skip. La mediana de <c>reels_skip_rate</c> medida el
/// 10-sep-2026 es 55 % — más de la mitad de la gente pasa de largo — y el cuartil
/// que menos skipea hace 7,6× las views del que más. Arrancar en negro regalaba
/// ese instante: el reel de tráiler tenía <c>fade=t=in:st=0:d=0.4</c> sobre el
/// video y el motion-card <c>fade=t=in:st=0:d=0.6</c> sobre el fondo.
/// </summary>
public class FirstFrameTests
{
    [Fact]
    public void TrailerReel_StartsAtFullBrightness_NoFadeFromBlack()
    {
        var args = InstagramVideoService.BuildTrailerReelArguments(
            "t.mp4", "hook.png", "ov.png", ["kp.jpg"], "out.mp4", 30);

        // Ningún fade de VIDEO desde negro…
        Assert.DoesNotContain("fade=t=in:st=0:d=0.4", args);
        Assert.DoesNotContain("fade=t=in:st=0:d=0.6", args);
        // …y el trim del segmento del tráiler sigue en su lugar
        Assert.Contains("trim=duration=30.0,setpts=PTS-STARTPTS[seg0]", args);
        // El fade del TEXTO (canal alpha, arranca a los 0.5s) no se toca: es la
        // animación de marca, no un arranque en negro.
        Assert.Contains("fade=t=in:st=0.5:d=0.8:alpha=1", args);
    }

    [Fact]
    public void MotionCard_StartsAtFullBrightness_NoFadeFromBlack()
    {
        var plain   = InstagramVideoService.BuildFfmpegArguments("in.png", "out.mp4", 12);
        var layered = InstagramVideoService.BuildFfmpegArguments(
            "bg.jpg", "out.mp4", 12, overlayPath: "overlay.png");

        foreach (var args in new[] { plain, layered })
        {
            Assert.DoesNotContain("fade=t=in:st=0:d=0.6", args);
            // El Ken Burns sigue intacto — lo que se sacó es solo el fade
            Assert.Contains("zoompan=z=", args);
            Assert.Contains("format=yuv420p", args);
        }

        // Con overlay, la animación del texto sobrevive
        Assert.Contains("fade=t=in:st=0.5:d=0.8:alpha=1", layered);
        // Y el fade de AUDIO tampoco se toca (nada que ver con el primer frame)
        Assert.Contains("afade=t=in:st=0:d=0.8",
            InstagramVideoService.BuildFfmpegArguments("in.png", "o.mp4", 12, musicPath: "m.mp3"));
    }
}

/// <summary>
/// Composición full-bleed del reel de tráiler: el propio clip, desenfocado y
/// ampliado, hace de fondo, y la banda nítida va centrada encima. Antes el fondo
/// era un panel abismo fijo y la banda ocupaba 900 de 1920 px — el 47 % de la
/// pantalla flotando sobre un fondo muerto.
/// </summary>
public class FullBleedTrailerTests
{
    private static string Args(double seconds = 30) =>
        InstagramVideoService.BuildTrailerReelArguments(
            "t.mp4", "hook.png", "ov.png", ["cta.jpg"], "out.mp4", seconds);

    [Fact]
    public void TheTrailerIsDecodedOnceAndUsedTwice()
    {
        var args = Args();

        // Una sola decodificación, dos ramas
        Assert.Contains("[0:v]fps=30,split=2[src][blurbase]", args);
        // El fondo YA NO es una imagen aparte: no hay input de bg
        Assert.DoesNotContain("bg.jpg", args);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(args, @"-i ""t\.mp4"""));
    }

    [Fact]
    public void BackgroundIsBlurredSmallThenUpscaled()
    {
        var args = Args();

        // El blur se calcula en 270×480 (≈16× más barato que a 1080×1920) y
        // recién después se amplía a pantalla completa
        Assert.Contains("[blurbase]scale=270:480:force_original_aspect_ratio=increase", args);
        Assert.Contains("crop=270:480,gblur=sigma=10", args);
        Assert.Contains("scale=1080:1920:flags=bicubic", args);
        // El gblur nunca corre a resolución completa
        Assert.DoesNotContain("scale=1080:1920:flags=bicubic,gblur", args);
    }

    [Fact]
    public void HookLayerHasNoFade_EditorialLayerDoes()
    {
        var args = Args();

        // El gancho se compone tal cual, desde el frame 0
        Assert.Contains("[1:v]format=rgba[hk]", args);
        Assert.Contains("[base][hk]overlay=x=0:y=0[hooked]", args);
        // El bloque editorial sí entra animado, encima del gancho
        Assert.Contains("[2:v]format=rgba,fade=t=in:st=0.5:d=0.8:alpha=1[ov]", args);
        Assert.Contains("[hooked][ov]overlay=", args);
    }

    [Theory]
    // Proporcional al largo: los PV oficiales abren con logos de distribuidora
    // que duran 3-6 s, así que en un tráiler largo saltearse 1,5 s era regalarle
    // el arranque del reel a un logo.
    [InlineData(120, 6.0)]   // techo
    [InlineData(90, 6.0)]    // 10.8 → techo
    [InlineData(30, 3.6)]
    [InlineData(17, 2.0)]    // teaser corto
    [InlineData(8, 1.5)]     // piso
    [InlineData(0, 1.5)]     // duración desconocida → piso
    public void StartSkipScalesWithDuration(double duration, double expected)
        => Assert.Equal(expected, InstagramVideoService.TrailerStartSkipFor(duration));

    [Fact]
    public void StartSkipReachesTheFilterGraph()
    {
        var args = InstagramVideoService.BuildTrailerReelArguments(
            "t.mp4", "hook.png", "ov.png", [], "out.mp4", 26, startSkip: 4.8);

        Assert.Contains("-ss 4.8 -i \"t.mp4\"", args);
    }
}

/// <summary>
/// El gancho del primer frame: 3-6 palabras enormes. Lo escribe la IA, y sin él
/// se derivan las primeras palabras del titular — el titular ENTERO no sirve,
/// porque ~80 caracteres se rompen en 3-5 líneas chicas.
/// </summary>
/// <summary>
/// La frase del pie del reel entra COMPLETA o no se pone. Antes usaba el Lede,
/// que está escrito para el caption (~110 caracteres) y sobre el video no
/// entraba: salía "…está en producción temprana, con una ventana de…" y ahí
/// terminaba, que se lee como un error y no como un recorte.
/// </summary>
public class FooterTextTests
{
    private static AnimeIndex.Scraper.Infrastructure.AiRewrite.NewsContent With(
        string? resumen, string? lede) =>
        new("Titular cualquiera", lede, [], "cuerpo", [], FromAi: true, Resumen: resumen);

    [Fact]
    public void PrefersTheResumen_WrittenForTheVideo()
        => Assert.Equal("MAPPA confirmó la cuarta temporada para 2027.",
            AnimeNewsImageService.FooterTextFor(
                With("MAPPA confirmó la cuarta temporada para 2027.", "un lede cualquiera")));

    [Fact]
    public void FallsBackToTheLede_OnlyIfItAlreadyFits()
    {
        // Corto: sirve
        Assert.Equal("El estudio lo confirmó hoy.",
            AnimeNewsImageService.FooterTextFor(With(null, "El estudio lo confirmó hoy.")));

        // Largo: NO se recorta, se descarta — mejor el hueco que el "…"
        var largo = new string('a', 120);
        Assert.Null(AnimeNewsImageService.FooterTextFor(With(null, largo)));
    }

    [Theory]
    // Nada usable → nada dibujado
    [InlineData(null, null)]
    [InlineData("", "")]
    public void ReturnsNullWhenThereIsNothingThatFits(string? resumen, string? lede)
        => Assert.Null(AnimeNewsImageService.FooterTextFor(With(resumen, lede)));

    [Fact]
    public void RejectsTextThatAlreadyCameTruncated()
    {
        // Si el candidato YA viene con puntos suspensivos, ponerlo sobre el video
        // reintroduce exactamente el problema que este campo vino a resolver.
        Assert.Null(AnimeNewsImageService.FooterTextFor(
            With(null, "El estudio confirmó que la película está en producción temprana, con una ventana de…")));
    }
}

public class HookTextTests
{
    private static AnimeIndex.Scraper.Infrastructure.AiRewrite.NewsContent With(
        string headline, string? hook) =>
        new(headline, null, [], "cuerpo", [], FromAi: true, Hook: hook);

    [Fact]
    public void UsesTheAiHookWhenItFits()
        => Assert.Equal("Jujutsu Kaisen vuelve",
            AnimeNewsImageService.HookTextFor(
                With("Jujutsu Kaisen confirma su cuarta temporada para 2027", "Jujutsu Kaisen vuelve")));

    [Theory]
    // Sin hook (heurística, cuota agotada, o el modelo ignoró el campo)
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Y con un hook que se fue de largo: deja de ser gancho, se descarta
    [InlineData("Jujutsu Kaisen confirma oficialmente su cuarta temporada")]
    public void FallsBackToTheFirstWordsOfTheHeadline(string? hook)
    {
        var text = AnimeNewsImageService.HookTextFor(
            With("Jujutsu Kaisen confirma su cuarta temporada para 2027", hook));

        // Las primeras palabras nombran la obra, que es lo que importa
        Assert.StartsWith("Jujutsu Kaisen", text);
        Assert.True(text.Length <= 30, $"gancho de {text.Length} caracteres: {text}");
    }

    [Theory]
    // Un separador suelto al inicio dejaba el token vacío y CORTABA el loop, así
    // que el fallback devolvía el titular entero (~80 caracteres) y WrapFit lo
    // truncaba en seco a 2 líneas, sin puntos suspensivos.
    [InlineData("— Confirmado: Jujutsu Kaisen vuelve en 2027", "Confirmado")]
    // El "de" colgando se cae por la regla de conectores, como corresponde
    [InlineData("- Nuevo tráiler de Solo Leveling", "Nuevo tráiler")]
    public void SkipsLeadingSeparators_InsteadOfFallingBackToTheWholeHeadline(
        string headline, string expected)
    {
        var text = AnimeNewsImageService.HookTextFor(With(headline, null));

        Assert.Equal(expected, text);
        Assert.True(text.Length <= 30, $"gancho de {text.Length} caracteres: {text}");
    }

    [Fact]
    public void NeverReturnsEmpty_EvenWithAOneWordHeadline()
    {
        Assert.Equal("Berserk", AnimeNewsImageService.HookTextFor(With("Berserk", null)));
        // Una sola palabra larguísima no entra en el presupuesto pero igual sale
        Assert.NotEqual("", AnimeNewsImageService.HookTextFor(
            With("Supercalifragilisticoexpialidosisaurio", null)));
    }

    [Theory]
    // La coma cierra la cláusula y ahí está el gancho — no se corta por conteo
    [InlineData("Murió Kentaro Miura, el creador de Berserk", "Murió Kentaro Miura")]
    [InlineData("Chainsaw Man: la película ya tiene fecha", "Chainsaw Man")]
    // Y un conector colgando por el corte se cae ("… confirma su" → "… confirma")
    [InlineData("Jujutsu Kaisen confirma su cuarta temporada para 2027", "Jujutsu Kaisen confirma")]
    [InlineData("Solo Leveling estrena el tráiler de la temporada 3", "Solo Leveling estrena")]
    // Los pronombres átonos de los verbos pronominales también cuelgan: caso real
    // del 10-sep-2026, el gancho salía "MY HERO ACADEMIA SE"
    [InlineData("My Hero Academia se une a la Selección Japonesa de Fútbol", "My Hero Academia")]
    public void DerivedHookCutsAtTheClause(string headline, string expected)
        => Assert.Equal(expected, AnimeNewsImageService.HookTextFor(With(headline, null)));
}

/// <summary>
/// El caption abre pidiendo el share, no repitiendo el titular. IG corta a ~125
/// caracteres: ese renglón es lo único que se lee sin tocar "más", y hasta
/// sep-2026 se gastaba en el mismo texto que ya está quemado en el cover.
/// </summary>
public class ShareHookTests
{
    [Fact]
    public void PickShareHook_IsStableAcrossCallsAndVariesByHeadline()
    {
        const string a = "Jujutsu Kaisen confirma su cuarta temporada";
        const string b = "Free Fire suma un crossover con Attack on Titan";

        // Estable: la misma noticia da siempre el mismo gancho (no depende de
        // string.GetHashCode, que .NET aleatoriza por proceso)
        Assert.Equal(AnimeNewsPublisherService.PickShareHook(a),
                     AnimeNewsPublisherService.PickShareHook(a));

        // Y a lo largo del feed rota: 20 titulares distintos no pueden dar todos
        // el mismo renglón de apertura
        var variants = Enumerable.Range(0, 20)
            .Select(i => AnimeNewsPublisherService.PickShareHook($"{a} parte {i}"))
            .Distinct()
            .Count();
        Assert.True(variants > 1, "el gancho debería variar entre noticias");

        Assert.NotEqual(string.Empty, AnimeNewsPublisherService.PickShareHook(b));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("Una noticia cualquiera")]
    [InlineData("")]
    public void PickShareHook_AsksForTheShare_AndFitsThePreview(string seed)
    {
        // Los 125 caracteres del preview de IG tienen que alcanzar para el
        // gancho entero — si se corta, el pedido no se lee.
        var hook = AnimeNewsPublisherService.PickShareHook(seed);

        Assert.True(hook.Length <= 125, $"gancho demasiado largo: {hook.Length}");
        Assert.DoesNotContain("📰", hook);   // el titular ya está en el video
    }
}

/// <summary>
/// Zona segura de Instagram en las piezas 9:16. En Reels IG dibuja su propia UI
/// ENCIMA del video — caption, usuario, ticker de audio y la botonera derecha se
/// comen los ~420 px de abajo, el header y la cámara los ~250 de arriba — y en la
/// grilla del perfil el cover se recorta a 4:5. Hasta sep-2026 el titular se
/// anclaba al 7 % del borde inferior (y≈1786 de 1920), o sea que el bloque
/// titular+lede vivía casi entero debajo de la botonera y en la grilla no
/// aparecía. El test renderiza de verdad y mira los píxeles.
/// </summary>
public class InstagramSafeAreaTests
{
    // El cover sin foto rinde el panel abismo + scrim: todo lo que quede claro
    // en esa pieza es texto. Umbral de luminancia bien por encima del fondo
    // (abismo 0x0B1422 → ~19; el scrim, más oscuro todavía).
    private const int TextLuminance = 110;

    private static AnimeNewsImageService NewService() =>
        new(new NoHttpFactory(), Microsoft.Extensions.Logging.Abstractions.NullLogger<AnimeNewsImageService>.Instance);

    private static AnimeIndex.Api.Data.Entities.AnimeNewsItem Item() =>
        new() { Title = "Jujutsu Kaisen confirma su cuarta temporada", SourceKey = "test" };

    private static AnimeIndex.Scraper.Infrastructure.AiRewrite.NewsContent Content() =>
        new("Jujutsu Kaisen confirma su cuarta temporada para 2027",
            "El estudio MAPPA lo anunció junto al primer arte promocional de la nueva etapa",
            ["Sukuna vuelve como antagonista central", "El estreno quedó fijado para el invierno de 2027"],
            "cuerpo", ["jujutsukaisen"], FromAi: true);

    /// <summary>Cuenta píxeles claros (= texto) por fila.</summary>
    private static int[] TextPixelsPerRow(byte[] jpeg)
    {
        using var bmp = SkiaSharp.SKBitmap.Decode(jpeg);
        var rows = new int[bmp.Height];
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                // El alpha importa en las capas RGBA del reel (el JPEG del cover
                // siempre trae 255, así que el chequeo no lo afecta).
                if (p.Alpha < 128) continue;
                // Luma BT.601 — barato y suficiente para separar texto de fondo
                if ((299 * p.Red + 587 * p.Green + 114 * p.Blue) / 1000 >= TextLuminance) rows[y]++;
            }
        return rows;
    }

    [Fact]
    public async Task Story916Cover_KeepsAllTextOutOfInstagramsChrome()
    {
        var rows = TextPixelsPerRow(await NewService().GenerateStoryAsync(Item(), Content(), []));

        // 1920 − 420 = 1500: nada de texto de ahí para abajo…
        var belowSafe = rows.Skip(1500).Sum();
        Assert.True(belowSafe == 0,
            $"{belowSafe} píxeles de texto caen bajo la UI de IG (y ≥ 1500)");

        // …ni arriba del header/cámara (los primeros 250 px)
        var aboveSafe = rows.Take(250).Sum();
        Assert.True(aboveSafe == 0, $"{aboveSafe} píxeles de texto caen bajo el header de IG (y < 250)");

        // Y el titular SÍ se rindió: si no, el test pasaría con un lienzo vacío
        Assert.True(rows.Sum() > 5000, "no se rindió texto — el test no probaría nada");
    }

    [Fact]
    public async Task Story916Cover_SurvivesTheProfileGrid_4by5Crop()
    {
        var rows = TextPixelsPerRow(await NewService().GenerateStoryAsync(Item(), Content(), []));

        // La grilla del perfil recorta el cover a 4:5 centrado: 1080×1350 →
        // se pierden 285 px arriba y abajo. El titular tiene que sobrevivir.
        var inGrid = rows.Skip(285).Take(1350).Sum();
        Assert.Equal(rows.Sum(), inGrid);
    }

    [Fact]
    public async Task SquarePieces_KeepTheirTightMargin()
    {
        // Las piezas cuadradas (carrusel de feed) no tienen chrome encima: el
        // margen chico de siempre se conserva, no se les aplica el de reels.
        var slides = await NewService().GenerateCarouselSlidesAsync(Item(), Content(), [], maxKeyPoints: 2);
        var rows   = TextPixelsPerRow(slides[0]);

        // Con el margen de reels (22 %) el texto terminaría antes de y=842; con
        // el de feed (7 %) llega cerca de y=1004.
        var lastTextRow = Array.FindLastIndex(rows, n => n > 0);
        Assert.True(lastTextRow > 900, $"el cuadrado perdió su margen chico (última fila con texto: {lastTextRow})");
        Assert.True(lastTextRow < 1080, "el texto se sale del canvas");
    }

    [Fact]
    public void VideoReelLayers_SplitTheScreenBetweenHookAndHeadline()
    {
        // El gancho arriba (visible desde el frame 0) y el bloque editorial
        // abajo: entre los dos tiene que quedar libre la banda del tráiler,
        // que va centrada en el 46 % de la altura.
        var (hookPng, overlayPng) = NewService().GenerateVideoReelLayers(
            Content() with { Hook = "Jujutsu Kaisen vuelve" });

        var hook    = TextPixelsPerRow(hookPng);
        var overlay = TextPixelsPerRow(overlayPng);

        // Gancho: arriba, dentro de la zona segura, y nada por debajo del medio
        Assert.True(hook.Take(288).Sum() == 0, "el gancho invade el header de IG");
        Assert.True(hook.Sum() > 3000, "no se rindió el gancho");
        Assert.True(hook.Skip(900).Sum() == 0, "el gancho baja hasta la banda del tráiler");

        // Titular: abajo, y NADA bajo la UI de IG
        Assert.True(overlay.Skip(1500).Sum() == 0, "el titular cae bajo la botonera de IG");
        Assert.True(overlay.Sum() > 3000, "no se rindió el titular");
        Assert.True(overlay.Take(1000).Sum() == 0, "el titular sube hasta la banda del tráiler");
    }

    [Fact]
    public void VideoReelLayers_LeaveTheVideoBandCompletelyClean()
    {
        // El video tiene que verse LIMPIO: nada de texto encima. La banda ocupa
        // y=610..1210 (caja fija, ver InstagramVideoService), así que NINGUNA de
        // las dos capas puede tener un píxel ahí.
        //
        // Antes se solapaban: la banda se centraba en una fracción de la altura
        // y el pie se anclaba al borde inferior, así que con un clip 16:9 la
        // banda terminaba en y≈1264 y la línea de cuándo/dónde arrancaba en
        // y≈1198 — escrita sobre el tráiler. Solo se vio renderizando un reel
        // completo con video real.
        var (hook, overlay) = NewService().GenerateVideoReelLayers(
            Content() with
            {
                Hook = "Jujutsu Kaisen vuelve",
                Cuando = "1 de julio 2026",
                Donde = "Crunchyroll",
                Resumen = "MAPPA confirmó la cuarta temporada para el invierno de 2027.",
            });

        foreach (var (capa, rows) in new[]
                 {
                     ("gancho", TextPixelsPerRow(hook)),
                     ("pie", TextPixelsPerRow(overlay)),
                 })
        {
            var invaden = rows.Skip(610).Take(600).Sum();
            Assert.True(invaden == 0,
                $"la capa de {capa} escribe {invaden} píxeles sobre la banda de video (y 610-1210)");
        }
    }

    [Fact]
    public void VideoReelLayers_NeverDrawTheMusicCreditOnTheVideo()
    {
        // "Música: Hyperfun — Kevin MacLeod · CC BY 4.0" ocupaba un renglón del
        // reel con algo que al espectador no le dice nada. La atribución sigue
        // saliendo —CC BY obliga— pero como última línea del caption.
        var (hook, overlay) = NewService().GenerateVideoReelLayers(Content());

        // El pie ahora termina en la marca de agua: nada entre ella y el borde.
        var rows = TextPixelsPerRow(overlay);
        Assert.True(rows.Skip(1500).Sum() == 0, "algo cae bajo la botonera de IG");
        Assert.True(TextPixelsPerRow(hook).Skip(1500).Sum() == 0, "el gancho invade la botonera");
    }

    [Fact]
    public void VideoReelLayers_HookLayerIsReadableOverAnyFrame()
    {
        // El fondo dejó de ser el panel abismo controlado: ahora es un frame
        // cualquiera del tráiler. Sin scrim, el gancho desaparece sobre una
        // escena clara — así que la capa tiene que traer el suyo.
        var (hookPng, _) = NewService().GenerateVideoReelLayers(Content());

        using var bmp = SkiaSharp.SKBitmap.Decode(hookPng);
        // Arriba de todo, el scrim es casi opaco…
        Assert.True(bmp.GetPixel(20, 10).Alpha > 200, "falta el scrim superior");
        // …y hacia el medio ya se disolvió (el video tiene que verse)
        Assert.True(bmp.GetPixel(20, 900).Alpha < 20, "el scrim superior tapa la banda del tráiler");
    }

    /// <summary>Sin fotos que bajar, el renderer nunca pide un HttpClient.</summary>
    private sealed class NoHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("el test no debería descargar imágenes");
    }
}

public class ArticleVideoExtractionTests
{
    [Theory]
    // Embed clásico de WordPress
    [InlineData("<iframe src=\"https://www.youtube.com/embed/jXtG_lcR9P4?rel=0\"></iframe>", "jXtG_lcR9P4")]
    // Lazy-embed de kudasai: el id vive en el thumbnail /vi/{id}/
    [InlineData("<img src=\"https://img.youtube.com/vi/jXtG_lcR9P4?showinfo=0/hqdefault.jpg\">", "jXtG_lcR9P4")]
    [InlineData("Mirá el tráiler: https://youtu.be/dQw4w9WgXcQ acá", "dQw4w9WgXcQ")]
    [InlineData("<a href=\"https://www.youtube.com/watch?v=abc123XYZ_-\">tráiler</a>", "abc123XYZ_-")]
    public void ExtractArticleVideoUrl_FindsTrailer(string html, string expectedId)
        => Assert.Equal($"https://www.youtube.com/watch?v={expectedId}",
            AnimeNewsFeedService.ExtractArticleVideoUrl(html));

    [Fact]
    public void ExtractArticleVideoUrl_IgnoresChannelLinksAndReturnsNullWithoutVideo()
    {
        // El link al canal de la fuente NO es un tráiler
        Assert.Null(AnimeNewsFeedService.ExtractArticleVideoUrl(
            "<a href=\"https://youtube.com/c/kudasai\">nuestro canal</a>"));
        Assert.Null(AnimeNewsFeedService.ExtractArticleVideoUrl("<p>sin video</p>"));
    }
}

public class ReelMusicServiceTests
{
    [Theory]
    [InlineData(new[] { "Acción", "Shounen" }, "epic")]
    [InlineData(new[] { "Comedia" }, "upbeat")]
    [InlineData(new[] { "Romance", "Drama" }, "emotional")]
    [InlineData(new[] { "Horror", "Mystery" }, "dark")]
    [InlineData(new[] { "Slice of Life" }, "chill")]
    // dark gana sobre epic cuando conviven (el terror define el tono)
    [InlineData(new[] { "Acción", "Horror" }, "dark")]
    [InlineData(new string[0], "chill")]
    public void HeuristicMood_MapsGenres(string[] genres, string expected)
        => Assert.Equal(expected, ReelMusicService.HeuristicMood(genres));

    [Fact]
    public void PickTrack_IsDeterministicPerSeriesAndRespectsMood()
    {
        var first  = ReelMusicService.PickTrack("one-piece", "epic");
        var second = ReelMusicService.PickTrack("one-piece", "epic");

        Assert.Equal(first, second);       // misma serie → mismo track siempre
        Assert.Equal("epic", first.Mood);  // respeta el mood pedido
    }

    [Fact]
    public void Library_EveryMoodHasEnoughTracksForMonthlyVariety()
    {
        // Con reel diario, ≥10 tracks por mood ⇒ ≥10 días entre repeticiones
        foreach (var mood in new[] { "epic", "dark", "upbeat", "chill", "emotional" })
            Assert.True(
                ReelMusicService.Library.Count(t => t.Mood == mood) >= 10,
                $"mood '{mood}' necesita al menos 10 tracks");
    }

    [Fact]
    public void PickTrackRotating_NeverRepeatsUntilMoodExhausted()
    {
        var epicCount = ReelMusicService.Library.Count(t => t.Mood == "epic");

        // Días consecutivos → tracks todos distintos hasta agotar el mood
        var seen = Enumerable.Range(0, epicCount)
            .Select(day => ReelMusicService.PickTrackRotating("epic", daySeed: day).Title)
            .ToList();
        Assert.Equal(epicCount, seen.Distinct().Count());

        // Mismo día → mismo track (retries idempotentes)
        Assert.Equal(
            ReelMusicService.PickTrackRotating("dark", daySeed: 42).Title,
            ReelMusicService.PickTrackRotating("dark", daySeed: 42).Title);
    }

    [Fact]
    public void Library_CcTracksCarryRequiredAttribution()
    {
        // El crédito CC BY es obligatorio — va como texto chico DENTRO del
        // video (nunca en el caption), sin emoji (SkiaSharp no tiene el glifo).
        Assert.All(ReelMusicService.Library, t =>
        {
            Assert.Contains("CC BY 4.0", t.Attribution);
            Assert.DoesNotContain("🎵", t.Attribution);
        });
    }

    [Theory]
    // Convención {mood}-{n} desde el public id de Cloudinary
    [InlineData("ig/music/epic-1", "epic")]
    [InlineData("ig/music/dark-taiko", "dark")]
    // Mood desconocido → chill (no rompe, suena neutro)
    [InlineData("ig/music/rocanrol-1", "chill")]
    public void ParseTrackFromPublicId_ReadsMoodFromName(string publicId, string expectedMood)
    {
        var track = ReelMusicService.ParseTrackFromPublicId(publicId, "https://res.cloudinary.com/x/a.mp3");

        Assert.NotNull(track);
        Assert.Equal(expectedMood, track.Mood);
        // Track propio → sin crédito (no hay obligación legal ni línea de marca)
        Assert.Null(track.Attribution);
    }

    [Fact]
    public void ParseTrackFromPublicId_NullUrlReturnsNull()
        => Assert.Null(ReelMusicService.ParseTrackFromPublicId("ig/music/epic-1", null));

    [Theory]
    [InlineData("Confirmado: la película de Chainsaw Man ya tiene fecha de estreno", "epic")]
    [InlineData("Fallece el mangaka de Berserk a los 54 años", "emotional")]
    [InlineData("Cancelado el anime de X tras la polémica", "dark")]
    [InlineData("El evento de figuras más grande de LATAM llega a Buenos Aires", "upbeat")]
    // el luto pisa al anuncio si conviven
    [InlineData("Estreno póstumo: homenaje al creador fallecido", "emotional")]
    public void HeuristicNewsMood_MapsHeadlines(string headline, string expected)
        => Assert.Equal(expected, ReelMusicService.HeuristicNewsMood(headline));

    [Fact]
    public void PickTrack_UsesCustomLibraryWhenProvided()
    {
        var custom = new[]
        {
            new ReelTrack("epic-suno-1", "https://cdn/x.mp3", "epic"),
            new ReelTrack("chill-suno-1", "https://cdn/y.mp3", "chill"),
        };

        var pick = ReelMusicService.PickTrack("one-piece", "epic", custom);
        Assert.Equal("epic-suno-1", pick.Title);
    }

    [Fact]
    public void FallbackStyleFor_RotatesByDayAndIsInstrumental()
    {
        // Días distintos → estilos distintos (ni el fallback suena siempre igual)
        var day1 = ReelMusicService.FallbackStyleFor("epic", daySeed: 1);
        var day2 = ReelMusicService.FallbackStyleFor("epic", daySeed: 2);
        Assert.NotEqual(day1, day2);

        // Todos los estilos de todos los moods piden instrumental (sin voz)
        foreach (var mood in new[] { "epic", "dark", "upbeat", "chill", "emotional" })
            for (var d = 0; d < 3; d++)
                Assert.Contains("instrumental", ReelMusicService.FallbackStyleFor(mood, d));
    }
}

public class AnimeNewsFeedSanitizeXmlTests
{
    [Fact]
    public void SanitizeXml_FixesRealWorldKudasaiTitleAndParses()
    {
        // Título real que rompía el feed de kudasai (jul 2026): reproducido en
        // vivo con XDocument.Parse antes del fix → XmlException/EntityName.
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0"><channel>
            <item><title>Monogatari Series: Off & Monster Season anuncia un nuevo episodio centrado en Karen</title></item>
            </channel></rss>
            """;

        Assert.Throws<System.Xml.XmlException>(() => System.Xml.Linq.XDocument.Parse(xml));

        var sanitized = AnimeNewsFeedService.SanitizeXml(xml);
        var doc = System.Xml.Linq.XDocument.Parse(sanitized); // no debe tirar

        var title = doc.Root!.Descendants("title").First().Value;
        Assert.Equal("Monogatari Series: Off & Monster Season anuncia un nuevo episodio centrado en Karen", title);
    }

    [Fact]
    public void SanitizeXml_LeavesWellFormedEntitiesUntouched()
    {
        var xml = "<a>&amp; &lt; &gt; &quot; &apos; &#39; &#x2019;</a>";
        Assert.Equal(xml, AnimeNewsFeedService.SanitizeXml(xml));
    }

    [Fact]
    public void SanitizeXml_EscapesMultipleBareAmpersands()
    {
        var xml = "<title>Naruto & Sasuke vs Boruto & Kawaki</title>";
        var result = AnimeNewsFeedService.SanitizeXml(xml);

        Assert.Equal("<title>Naruto &amp; Sasuke vs Boruto &amp; Kawaki</title>", result);
        System.Xml.Linq.XDocument.Parse(result); // no debe tirar
    }
}

public class HeroEpisodeTests
{
    private static AnimeIndex.Api.Data.Entities.Series MakeSeries(decimal? score = null, string type = "tv") =>
        new() { Slug = "x", Title = "X", Score = score, Type = type };

    private static AnimeIndex.Api.Data.Entities.Episode MakeEpisode(short number) =>
        new() { EpisodeNumber = number };

    [Fact]
    public void HeuristicEpisodeScore_PremiereOfTopSeriesBeatsMidSeasonFiller()
    {
        // Estreno (ep. 1) de serie top — el caso "Mushoku Tensei 3" del usuario
        var premiere = InstagramPublisherService.HeuristicEpisodeScore(
            MakeSeries(score: 8.5m), MakeEpisode(1));
        // Episodio intermedio de una serie mediocre
        var filler = InstagramPublisherService.HeuristicEpisodeScore(
            MakeSeries(score: 6.2m), MakeEpisode(11));

        Assert.True(premiere > filler, $"estreno ({premiere}) debe superar al relleno ({filler})");
    }

    [Fact]
    public void HeuristicEpisodeScore_MovieOutranksRegularEpisode()
    {
        var movie   = InstagramPublisherService.HeuristicEpisodeScore(
            MakeSeries(score: 7.5m, type: "movie"), MakeEpisode(1));
        var regular = InstagramPublisherService.HeuristicEpisodeScore(
            MakeSeries(score: 7.5m), MakeEpisode(8));

        Assert.True(movie > regular);
    }
}

public class NewsRelevanceTests
{
    [Fact]
    public void HeuristicNewsScore_RanksBigNewsAboveMinorOnes()
    {
        var estreno   = AnimeNewsPublisherService.HeuristicNewsScore(
            "Confirmado: la película de Jujutsu Kaisen tiene fecha de estreno y tráiler");
        var figura    = AnimeNewsPublisherService.HeuristicNewsScore(
            "Nueva figura coleccionable de un personaje secundario");
        var fallecido = AnimeNewsPublisherService.HeuristicNewsScore(
            "Fallece reconocido animador del estudio");

        Assert.True(estreno > figura, $"estreno ({estreno}) debería superar a figura ({figura})");
        Assert.True(fallecido > figura, $"luto ({fallecido}) debería superar a figura ({figura})");
    }

    [Fact]
    public void HeuristicNewsScore_PutsCrossoversOnTop_TheRealPeaks()
    {
        // Los dos picos históricos de la cuenta son cruces con audiencias
        // masivas de AFUERA del anime — Free Fire × anime (35.192 views) y The
        // Ninth Jedi de Star Wars (27.622) — y hasta sep-2026 la heurística no
        // tenía ni una de esas palabras, o sea que les daba 0 mientras eran lo
        // mejor que publicamos.
        var freeFire = AnimeNewsPublisherService.HeuristicNewsScore(
            "Free Fire anuncia una colaboración con Attack on Titan");
        var starWars = AnimeNewsPublisherService.HeuristicNewsScore(
            "Star Wars: The Ninth Jedi presenta su serie anime");
        var nicho = AnimeNewsPublisherService.HeuristicNewsScore(
            "Una novela ligera poco conocida confirma adaptación al anime");

        Assert.True(freeFire > nicho, $"crossover de gaming ({freeFire}) vs nicho ({nicho})");
        Assert.True(starWars > nicho, $"crossover occidental ({starWars}) vs nicho ({nicho})");
    }

    [Fact]
    public void HeuristicNewsScore_RejectsBrandMentionsOutsideTheAnimeWorld_RealCase()
    {
        // Caso REAL: "ANÁLISIS – Marvel's Wolverine" (feed de Crunchyroll) se
        // llevó los 8 puntos de crossover por nombrar a Marvel y SALIÓ PUBLICADO
        // el 10-sep-2026 (run 34493207860). Es una reseña de videojuego, sin
        // nada de anime. La marca sola no es un cruce.
        var resenaDeJuego = AnimeNewsPublisherService.HeuristicNewsScore("ANÁLISIS – Marvel's Wolverine");
        var cruceReal = AnimeNewsPublisherService.HeuristicNewsScore(
            "Marvel anuncia una colaboración con el anime de My Hero Academia");

        Assert.True(cruceReal > resenaDeJuego,
            $"el cruce real ({cruceReal}) tiene que ganarle a la reseña de juego ({resenaDeJuego})");
        Assert.True(resenaDeJuego <= 0, $"la reseña de juego no debería puntuar ({resenaDeJuego})");
    }

    [Theory]
    // Con contexto de anime, el cruce cuenta…
    [InlineData("Star Wars: The Ninth Jedi presenta su serie anime", true)]
    [InlineData("Free Fire anuncia una colaboración con Attack on Titan", true)]
    [InlineData("El manga de Fortnite llega en octubre", true)]
    // …sin él, no
    [InlineData("ANÁLISIS – Marvel's Wolverine", false)]
    [InlineData("Se filtró el tráiler de la nueva de Batman", false)]
    public void MentionsAnimeWorld_GatesTheCrossoverBonus(string title, bool expected)
        => Assert.Equal(expected, AnimeNewsPublisherService.MentionsAnimeWorld(
            TrailerDownloadService.Normalize(title)));

    [Fact]
    public void HeuristicNewsScore_DemotesOtherRegionNews_RealCase()
    {
        // Caso REAL: "[España] Studio Ghibli protagoniza una actividad del
        // programa Toma la palabra" salió publicado el 10-sep-2026 (run
        // 34496989827). Un evento de TV española que no le interesa a nadie en
        // México ni en Argentina, pero pasaba el gate de anime por nombrar a
        // Ghibli. La cuenta apunta a toda LATAM.
        var regional = AnimeNewsPublisherService.HeuristicNewsScore(
            "[España] Studio Ghibli protagoniza una actividad del programa Toma la palabra");
        var latam = AnimeNewsPublisherService.HeuristicNewsScore(
            "Studio Ghibli anuncia una nueva película para 2027");

        Assert.True(latam > regional, $"LATAM ({latam}) tiene que ganarle a la regional ({regional})");
    }

    [Fact]
    public void HeuristicNewsScore_DoesNotTreatDistributorsAsCrossovers()
    {
        // "llega a Netflix" es dónde se ve, no un cruce de audiencias. Si contara
        // como crossover, cualquier noticia rutinaria de licencias se comería los
        // 8 puntos y desplazaría a los cruces de verdad.
        var licencia = AnimeNewsPublisherService.HeuristicNewsScore(
            "La serie llega a Netflix en octubre");
        var crossover = AnimeNewsPublisherService.HeuristicNewsScore(
            "La serie anuncia un crossover con Fortnite");

        Assert.True(crossover > licencia, $"crossover ({crossover}) vs licencia ({licencia})");
    }

    [Fact]
    public void HeuristicNewsScore_IsAccentInsensitive()
    {
        // El score normaliza el texto, así que "colaboración"/"colaboracion" y
        // "película"/"pelicula" valen lo mismo. Escribir las keywords con tilde
        // contra texto normalizado es el bug que ya mató la escalera de tráilers
        // durante dos semanas (ver StripSpanishSuffix).
        Assert.Equal(
            AnimeNewsPublisherService.HeuristicNewsScore("La película estrena una colaboración"),
            AnimeNewsPublisherService.HeuristicNewsScore("La pelicula estrena una colaboracion"));
    }
}

/// <summary>
/// El resumen que deja --insights-sync en los logs. Usa MEDIANA y no promedio a
/// propósito: la distribución es de cola larga (el top 10 se lleva el 39 % de
/// todas las views), así que el promedio no describe a ninguna pieza real.
/// </summary>
public class InsightsSummaryTests
{
    private static AnimeIndex.Api.Data.AppDbContext NewDb()
    {
        var opts = Microsoft.EntityFrameworkCore.InMemoryDbContextOptionsExtensions.UseInMemoryDatabase(
                new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AnimeIndex.Api.Data.AppDbContext>(),
                $"insights-{Guid.NewGuid():N}")
            .Options;
        return new AnimeIndex.Api.Data.AppDbContext(opts);
    }

    private static AnimeIndex.Scraper.Infrastructure.Instagram.NewsInsightsSyncService Svc(
        AnimeIndex.Api.Data.AppDbContext db) =>
        new(db, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                AnimeIndex.Scraper.Infrastructure.Instagram.NewsInsightsSyncService>.Instance);

    private static AnimeIndex.Api.Data.Entities.AnimeNewsItem Reel(
        long views, double? watch = null, double? duration = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            SourceKey = "test",
            RssGuid = Guid.NewGuid().ToString(),
            Title = $"Noticia de {views} views",
            ArticleUrl = "https://example.com",
            IgReelMediaId = "media-" + views,
            IgPostedAt = DateTime.UtcNow.AddDays(-1),
            IgReelViews = views,
            IgReelAvgWatchSeconds = watch,
            IgReelDurationSeconds = duration,
        };

    [Fact]
    public async Task Summary_ReportsMedianNotMean_SoOneHitDoesNotHideTheFloor()
    {
        using var db = NewDb();
        // Cuatro piezas normales y un pico — la forma real de la distribución.
        db.AnimeNewsItems.AddRange(Reel(200), Reel(300), Reel(250), Reel(280), Reel(35000));
        await db.SaveChangesAsync();

        var summary = await Svc(db).SummaryAsync();

        // Mediana 280 (el promedio sería 7.206, que no describe a ninguna)
        Assert.Contains("views mediana 280", summary);
        Assert.Contains("total 36030", summary);
        Assert.Contains("5 reels", summary);
    }

    [Fact]
    public async Task Summary_ComputesRealRetention_NotBareWatchTime()
    {
        using var db = NewDb();
        // Mismo watch time, duraciones distintas: 6,9 s de 29,5 es 23 % y de
        // 55,5 es 12 %. Es EXACTAMENTE la confusión que la columna de duración
        // vino a resolver — watch time a secas los haría ver iguales.
        db.AnimeNewsItems.AddRange(
            Reel(500, watch: 6.9, duration: 29.5),
            Reel(500, watch: 6.9, duration: 55.5));
        await db.SaveChangesAsync();

        var summary = await Svc(db).SummaryAsync();

        // Mediana de 23,4 % y 12,4 % → 17,9 % → redondea a 18
        Assert.Contains("retención mediana 18 %", summary);
        Assert.Contains("n=2", summary);
    }

    [Fact]
    public async Task Summary_SaysSoWhenThereIsNothingToReport()
    {
        using var db = NewDb();
        Assert.Equal("sin reels medidos todavía", await Svc(db).SummaryAsync());

        // Y con reels publicados pero sin duración guardada (los de antes del
        // sprint 3), la retención no se puede calcular — pero las views sí.
        db.AnimeNewsItems.Add(Reel(400, watch: 5.0, duration: null));
        await db.SaveChangesAsync();

        var summary = await Svc(db).SummaryAsync();
        Assert.Contains("retención mediana sin datos", summary);
        Assert.Contains("views mediana 400", summary);
    }
}

/// <summary>
/// La duración renderizada viaja con el MP4 porque NO se puede recuperar
/// después: la API de insights no la expone y avg_watch_time es un promedio, no
/// la duración. Sin ella solo se puede mirar watch time absoluto, que está
/// acotado por la duración — que es justo lo que hacía parecer dos hallazgos
/// distintos a lo que en buena medida era el mismo.
/// </summary>
public class RenderedDurationTests
{
    [Theory]
    // n escenas de 4 s solapadas 0,6 s: n·4 − (n−1)·0,6
    [InlineData(2, 7.4)]
    [InlineData(3, 10.8)]
    [InlineData(5, 17.6)]
    public void SlideshowSeconds_MatchesTheFilterGraph(int slides, double expected)
    {
        Assert.Equal(expected, InstagramVideoService.SlideshowSeconds(slides));

        // Y coincide con el -t que se le pasa a ffmpeg — si divergieran,
        // guardaríamos en la DB una duración que el video no tiene.
        var args = InstagramVideoService.BuildSlideshowArguments(
            [.. Enumerable.Range(0, slides).Select(i => $"s{i}.jpg")], "out.mp4");
        Assert.Contains($"-t {expected.ToString(System.Globalization.CultureInfo.InvariantCulture)} ", args);
    }

    [Fact]
    public void TrailerReelDuration_MatchesTheFilterGraph()
    {
        // 26 s de tráiler + 1 slide de CTA de 3,5 = 29,5 (el objetivo del sprint 2)
        var args = InstagramVideoService.BuildTrailerReelArguments(
            "t.mp4", "hook.png", "ov.png", ["cta.jpg"], "out.mp4", 26);

        Assert.Contains("-t 29.5", args);
    }
}

public class TrailerSearchTests
{
    private static string Line(string id, string duration, string title, string channel) =>
        $"{id}|~|{duration}|~|{title}|~|{channel}";

    [Fact]
    public void PickBestSearchResult_PicksSpanishOfficialTrailerOverFanContent()
    {
        // Resultados REALES de la búsqueda "Crunchyroll en español trailer doblaje
        // español latino" (jul-2026) + contenido fan que debe quedar afuera.
        var best = TrailerDownloadService.PickBestSearchResult(
        [
            Line("dR7DW4ykE8k", "131", "Solo Leveling en ESPAÑOL | TRÁILER OFICIAL", "Crunchyroll en Español"),
            Line("fanreaccion1", "300", "REACCIÓN al tráiler de Solo Leveling en español", "ReactBro LATAM"),
            Line("fanexplica01", "600", "Solo Leveling temporada 2 explicado en español", "OtakuFan"),
        ]);

        Assert.Equal("dR7DW4ykE8k", best?.Id);
        // La duración viaja con el candidato: define cuánto tráiler muestra el reel
        Assert.Equal(131, best?.DurationSeconds);
    }

    [Fact]
    public void PickBestSearchResult_RejectsNonSpanishResults_LanguageRule()
    {
        // Resultados REALES de "Sword Art Online Integral Domain official trailer"
        // (jul-2026): TODO en inglés/japonés, incluido el teaser oficial de Aniplex.
        // Requisito del usuario: el video tiene que estar en español (doblaje o
        // subs incrustados) — sin versión latina, mejor slideshow que PV japonés.
        var best = TrailerDownloadService.PickBestSearchResult(
        [
            Line("UHWMxtRivt8", "17", "Sword Art Online the Movie - Integral Domain -  |  COMING 2028", "Aniplex USA"),
            Line("a2_XZColIY4", "17", "Sword Art Online Integral Domain Anime Movie - Official Teaser", "Anime Officials Trailer"),
            Line("japanesepv01", "NA", "TVアニメ『葬送のフリーレン』本予告", "TOHO animation チャンネル"),
        ]);

        Assert.Null(best);
    }

    [Fact]
    public void PickBestSearchResult_RelaxedLanguage_PicksOfficialUpload()
    {
        // 2do intento (sin versión latina): se relaja SOLO el idioma para buscar
        // un tráiler oficial al que quemarle subtítulos es manuales. El upload
        // de Aniplex gana aunque el título no diga "trailer" (bonus por canal).
        var best = TrailerDownloadService.PickBestSearchResult(
        [
            Line("UHWMxtRivt8", "17", "Sword Art Online the Movie - Integral Domain -  |  COMING 2028", "Aniplex USA"),
            Line("fanmade00001", "16", "SAO Integral Domain Official Trailer concept", "KingYan Animation Studio"),
        ], requireSpanish: false);

        Assert.Equal("UHWMxtRivt8", best?.Id);
    }

    [Fact]
    public void PickBestSearchResult_RejectsLongVideosEvenWithTrailerInTitle()
    {
        // >6 min no es un tráiler (episodio/compilado/live), aunque el título diga tráiler
        Assert.Null(TrailerDownloadService.PickBestSearchResult(
            [Line("longvideo001", "1800", "Todos los tráilers de anime en español 2028", "Recopilador")]));
    }

    [Fact]
    public void PickBestSearchResult_RequiresTrailerSignal()
    {
        // En español pero sin señal de tráiler (score < 4) → sin confianza,
        // mejor slideshow que incrustar el video equivocado
        Assert.Null(TrailerDownloadService.PickBestSearchResult(
            [Line("randomvid001", "90", "Sword Art Online opening completo en español", "MusicChannel")]));
        Assert.Null(TrailerDownloadService.PickBestSearchResult([]));
    }

    [Theory]
    // Titulares que anuncian material audiovisual → buscar (versión latina).
    // La query es la OBRA (palabras significativas del titular) + el sufijo:
    // el titular completo como query devolvía 0 resultados en prod (jul-2026).
    [InlineData("Sword Art Online anuncia nueva película para 2028", "Sword Art Online")]
    [InlineData("Frieren confirma su segunda temporada con un tráiler", "Frieren")]
    [InlineData("El live-action de One Piece ya tiene fecha de estreno", "One Piece")]
    // La raíz "estren" cubre las conjugaciones (bug real jul-2026: "se
    // estrenará" no disparaba porque se buscaba el sustantivo "estreno")
    [InlineData("La segunda parte de Chitose se estrenará en octubre", "Chitose")]
    public void HeuristicVideoQuery_BuildsCleanTrailerQuery(string title, string obra)
    {
        var result = AnimeNewsPublisherService.HeuristicVideoQuery(title);

        Assert.NotNull(result);
        Assert.Equal(NewsVideoKind.Trailer, result!.Value.Kind);
        // La obra al frente, sin el ruido del titular, y el sufijo LATAM al final
        Assert.StartsWith(obra, result!.Value.Query);
        Assert.EndsWith("tráiler oficial español latino", result!.Value.Query);
    }

    [Theory]
    // Titulares sin video que buscar → null (queda el slideshow)
    [InlineData("Sword Art Online anuncia una nueva novela sobre Kirito y Asuna")]
    [InlineData("Fallece reconocido animador del estudio Ghibli")]
    [InlineData("Nueva figura coleccionable de Nezuko agota su preventa")]
    public void HeuristicVideoQuery_IgnoresNonAudiovisualNews(string title)
        => Assert.Null(AnimeNewsPublisherService.HeuristicVideoQuery(title));

    [Theory]
    // Los casos REALES que salieron sin video (jul-2026): noticias de
    // openings/endings/cortos no eran "de tráiler" y quedaban en slideshow
    [InlineData("Mob y Reigen regresan: Mob Psycho 100 estrena un corto animado", NewsVideoKind.Short)]
    [InlineData("Yoroi-Shinden Samurai Troopers estrena opening y ending sin créditos", NewsVideoKind.ThemeSong)]
    [InlineData("Dannie May estrena video musical del opening de Yoroi-Shinden Samurai Troopers", NewsVideoKind.ThemeSong)]
    public void HeuristicVideoQuery_ClassifiesThemeAndShortNews(string title, NewsVideoKind expected)
    {
        var result = AnimeNewsPublisherService.HeuristicVideoQuery(title);

        Assert.NotNull(result);
        Assert.Equal(expected, result!.Value.Kind);
        // Para temas y cortos el idioma no aplica — la query no fuerza español
        Assert.DoesNotContain("español latino", result!.Value.Query);
    }

    [Theory]
    // Las corridas de carrusel común postergan las noticias con video
    [InlineData("Frieren confirma su segunda temporada con un tráiler", true)]
    [InlineData("Samurai Troopers estrena opening y ending sin créditos", true)]
    [InlineData("Mob Psycho 100 estrena un corto animado", true)]
    [InlineData("El evento de figuras más grande de LATAM llega a Buenos Aires", false)]
    public void HasAudiovisualSignal_DetectsVideoNews(string title, bool expected)
        => Assert.Equal(expected, AnimeNewsPublisherService.HasAudiovisualSignal(title));

    [Fact]
    public void PickBestSearchResult_RejectsAutoTranslatedFanVideo_RealCase()
    {
        // Caso REAL (reel publicado 12-jul-2026): video fan en INGLÉS del canal
        // "Novagesis" ("The Most Unexpected Anime of 2026 Just Dropped Its First
        // Trailer"). YouTube auto-traduce títulos según la región del requester,
        // así que vía WARP el título llegó "en español" — y "tráiler" contaba
        // como señal de idioma. Debe caer en ambos modos: en español (el título
        // traducido no dice nada del idioma real) y relajado (no es oficial).
        var lines = new[]
        {
            Line("QanuAtmPzOM", "71",
                "El anime más inesperado de 2026 acaba de lanzar su primer tráiler", "Novagesis"),
        };

        Assert.Null(TrailerDownloadService.PickBestSearchResult(lines));
        Assert.Null(TrailerDownloadService.PickBestSearchResult(lines, requireSpanish: false));
    }

    [Fact]
    public void PickBestSearchResult_ShortKind_AcceptsOfficialSpecialMovie_RealCase()
    {
        // Caso REAL (Mob Psycho 100, 12-jul-2026): el corto de aniversario
        // embebido en el artículo ES el video de la noticia — upload oficial de
        // Warner Japan; con kind Short el idioma no aplica.
        var best = TrailerDownloadService.PickBestSearchResult(
            [Line("OWSfQwwMcE4", "127",
                "アニメ『モブサイコ100』10周年記念特別映像｜MOB PSYCHO 100 10th Anniversary Special Movie",
                "Warner Bros. Japan Anime")],
            requireSpanish: false, kind: NewsVideoKind.Short);

        Assert.Equal("OWSfQwwMcE4", best?.Id);
    }

    [Fact]
    public void PickBestSearchResult_RelaxedMode_AcceptsSubjectMatchedOpeningWithoutOfficialMarker()
    {
        // Líneas REALES del run 29656418763 (18-jul-2026): el creditless
        // opening OFICIAL de The Cat and the Dragon venía del canal japonés
        // 宝島 (sin "official/公式" en el nombre) y el gate lo rechazaba —
        // era EL video de la noticia y el reel salió sin video. Regla nueva:
        // sin oficial alcanza palabra del TIPO + obra VERIFICADA.
        var best = TrailerDownloadService.PickBestSearchResult(
        [
            Line("k3NwKLjfEOw", "230", "Cat Days", "suis from Yorushika - Topic"),
            Line("ZH8xYK5vac0", "91",
                "TV Anime \"Cat and Dragon\" Creditless Opening Movie [Broadcasting & Streaming since July 2026]",
                "宝島"),
        ], requireSpanish: false, kind: NewsVideoKind.ThemeSong, subject: "Cat and Dragon");

        Assert.Equal("ZH8xYK5vac0", best?.Id);

        // Y con el subject COMBINADO artista+obra que manda el publisher (el
        // upload matchea "cat dragon" aunque no mencione al artista): ≥2
        // tokens alcanzan — exigir mayoría del combinado lo rechazaba
        var combined = TrailerDownloadService.PickBestSearchResult(
            [Line("ZH8xYK5vac0", "91",
                "TV Anime \"Cat and Dragon\" Creditless Opening Movie [Broadcasting & Streaming since July 2026]",
                "宝島")],
            requireSpanish: false, kind: NewsVideoKind.ThemeSong,
            subject: "suis from Yorushika suis Yorushika Cat Dragon");
        Assert.Equal("ZH8xYK5vac0", combined?.Id);

        // SIN obra verificable, el requisito de oficial se mantiene (la
        // regresión Novagesis del 12-jul depende de esto)
        Assert.Null(TrailerDownloadService.PickBestSearchResult(
            [Line("ZH8xYK5vac0", "91", "TV Anime Creditless Opening Movie", "canal random")],
            requireSpanish: false, kind: NewsVideoKind.ThemeSong));
    }

    [Fact]
    public void PickBestSearchResult_ThemeKind_AcceptsCreditlessOpeningRejectsFanContent()
    {
        var best = TrailerDownloadService.PickBestSearchResult(
        [
            Line("fanamv000001", "95", "Samurai Troopers Opening [AMV]", "OtakuEdits"),
            Line("official0001", "92", "『鎧伝サムライトルーパーズ』ノンクレジットオープニング", "KADOKAWAanime"),
        ], requireSpanish: false, kind: NewsVideoKind.ThemeSong);

        Assert.Equal("official0001", best?.Id);
    }

    [Fact]
    public void PickBestSearchResult_RejectsWrongMovieTrailers_RealCases()
    {
        // Casos REALES (14-15 jul-2026): la obra no tenía tráiler en español y
        // YouTube rellenó los resultados con tráilers de CINE en español que
        // pasaban TODOS los filtros (canal Warner oficial + "tráiler" en el
        // título + duración de tráiler). Se publicaron reels con "Project X" y
        // "La Piel Que Habito" para Tsugumi Project, y "Faraway Downs" para
        // From Far Away. El gate de relevancia: la mayoría de los tokens de la
        // obra tiene que aparecer en el título/canal del resultado.
        Assert.Null(TrailerDownloadService.PickBestSearchResult(
            [Line("fMJ4IBnU0Ks", "85", "Project X - Tráiler Oficial Español HD", "Warner Bros. Pictures España")],
            subject: "Tsugumi Project"));

        Assert.Null(TrailerDownloadService.PickBestSearchResult(
            [Line("Gm8XTqv_K80", "32", "La Piel Que habito - Tráiler Oficial", "Warner Bros. Pictures España")],
            subject: "Tsugumi Project"));

        Assert.Null(TrailerDownloadService.PickBestSearchResult(
            [Line("9XZgjEnG_LE", "151", "FARAWAY DOWNS: AUSTRALIA Tráiler Español Latino (2023) Nicole Kidman, Hugh Jackman", "FilmSelect Español")],
            subject: "From Far Away"));

        Assert.Null(TrailerDownloadService.PickBestSearchResult(
            [Line("4HOrjGQhpV4", "101", "Contratiempo - Tráiler Oficial Castellano HD", "Warner Bros. Pictures España")],
            subject: "Mercedes and the Waning Moon"));
    }

    [Fact]
    public void PickBestSearchResult_SubjectGate_StillAcceptsTheRightTrailer()
    {
        // El mismo pool con el tráiler correcto presente: la obra matchea y gana
        var best = TrailerDownloadService.PickBestSearchResult(
        [
            Line("fMJ4IBnU0Ks", "85", "Project X - Tráiler Oficial Español HD", "Warner Bros. Pictures España"),
            Line("dR7DW4ykE8k", "131", "Solo Leveling en ESPAÑOL | TRÁILER OFICIAL", "Crunchyroll en Español"),
        ], subject: "Solo Leveling");

        Assert.Equal("dR7DW4ykE8k", best?.Id);

        // Y el subject también matchea contra el CANAL (uploads japoneses que
        // solo llevan el nombre en kanji en el título)
        var byChannel = TrailerDownloadService.PickBestSearchResult(
            [Line("jpchannel001", "90", "本予告", "Frieren Official Channel")],
            requireSpanish: false, subject: "Frieren");
        Assert.Equal("jpchannel001", byChannel?.Id);
    }

    [Theory]
    // La obra queda; el ruido del titular (verbos, medio, fechas, años) no
    [InlineData("Sword Art Online anuncia nueva película para 2028", "Sword Art Online")]
    [InlineData("El grupo ClariS estrenó el vídeo musical del opening de The Ogre's Bride", "ClariS Ogre Bride")]
    [InlineData("Mob y Reigen regresan: Mob Psycho 100 estrena un corto animado", "Mob Reigen Psycho 100")]
    public void SignificantWords_KeepsTheObraDropsTheNoise(string title, string expected)
        => Assert.Equal(expected, string.Join(' ', TrailerDownloadService.SignificantWords(title)));

    [Theory]
    // Caso REAL (16-jul-2026): la frase citada del titular quedaba en la query
    // ("BanG Dream YUME MITA lleno magia tráiler oficial español latino") y
    // YouTube devolvía 0 resultados; sin «lleno de magia» el 1er resultado era
    // el tráiler oficial en español. Las citas se descartan de la obra.
    [InlineData("BanG Dream! YUME∞MITA estrena tráiler y un arte \"lleno de magia\"", "BanG Dream YUME MITA")]
    [InlineData("Frieren estrena tráiler con un arte «digno de un grimorio»", "Frieren")]
    [InlineData("Kimetsu no Yaiba presenta “la batalla final” en su nuevo avance", "Kimetsu Yaiba")]
    // Sin citas la obra queda igual que con SignificantWords
    [InlineData("Sword Art Online anuncia nueva película para 2028", "Sword Art Online")]
    // Apóstrofos de títulos en inglés: NO son citas, no se tocan
    [InlineData("El opening de The Ogre's Bride llega con video musical", "Ogre Bride")]
    public void SubjectFromTitle_DropsQuotedSegments(string title, string expected)
        => Assert.Equal(expected, TrailerDownloadService.SubjectFromTitle(title));

    [Fact]
    public void HeuristicVideoQuery_DropsQuotedNoiseFromTrailerQuery_RealCase()
    {
        // El titular real del reel que salió sin video (16-jul-2026)
        var result = AnimeNewsPublisherService.HeuristicVideoQuery(
            "BanG Dream! YUME∞MITA estrena tráiler y un arte \"lleno de magia\"");

        Assert.NotNull(result);
        Assert.Equal(NewsVideoKind.Trailer, result!.Value.Kind);
        Assert.Equal("BanG Dream YUME MITA tráiler oficial español latino", result!.Value.Query);
    }

    [Fact]
    public void HeuristicVideoQuery_DetectsAccentedVideoMusical()
    {
        // "vídeo musical" (con tilde, como titula Crunchyroll) también es tema
        var result = AnimeNewsPublisherService.HeuristicVideoQuery(
            "MYTH & ROID comparte un vídeo musical especial de su nueva canción");

        Assert.NotNull(result);
        Assert.Equal(NewsVideoKind.ThemeSong, result!.Value.Kind);
    }
}

public class ExternalPostFallbackTests
{
    // Respaldo X/Twitter (18-jul-2026): la descarga de YouTube quedó bloqueada
    // desde CI (34 combos FAIL) pero X no bloquea a los runners. La URL la
    // encuentra la IA con grounding y se valida contra la metadata real.
    private const string TweetUrl = "https://x.com/crunchyroll_la/status/2067276084865884314";

    private static string Line(string id, string duration, string title, string uploader) =>
        $"{id}|~|{duration}|~|{title}|~|{uploader}";

    [Fact]
    public void EvaluateExternalPost_AcceptsOfficialCrunchyrollTweet_RealCase()
    {
        // Línea REAL del diag 18-jul (tweet fijado de Crunchyroll LATAM)
        var candidate = TrailerDownloadService.EvaluateExternalPost(TweetUrl,
            Line("2067045108524974080", "60.06",
                "Crunchyroll LATAM - ¡Se viene una nueva temporada llena de emociones! ☀️ Tus animes favoritos regresan",
                "Crunchyroll LATAM"),
            subject: "temporada julio anime");

        Assert.NotNull(candidate);
        Assert.Equal(TweetUrl, candidate!.Url);
        Assert.Equal(60.06, candidate.DurationSeconds, precision: 2);
    }

    [Fact]
    public void EvaluateExternalPost_AcceptsWorkAccountThatMentionsTheObra()
    {
        // Cuenta de la obra (no está en OfficialChannelRegex) — la mención de
        // la obra en texto+uploader alcanza
        var candidate = TrailerDownloadService.EvaluateExternalPost(TweetUrl,
            Line("111", "95", "The Ogre's Bride TV anime — main trailer", "Ogre Bride Anime"),
            subject: "Ogre Bride");

        Assert.NotNull(candidate);
    }

    [Theory]
    // Cuenta random sin mención de la obra → afuera (URL alucinada o post ajeno)
    [InlineData("222", "90", "mirá este video increíble", "RandomFanAccount", "Frieren")]
    // Contenido fan explícito → afuera aunque mencione la obra
    [InlineData("333", "90", "REACCIÓN al tráiler de Frieren!!", "Frieren Fans LATAM", "Frieren")]
    // Tweet de texto (sin video → duración NA=0) → afuera
    [InlineData("444", "NA", "Crunchyroll LATAM - ¡Gran anuncio mañana!", "Crunchyroll LATAM", "Frieren")]
    // Video demasiado largo (compilado/stream) → afuera
    [InlineData("555", "2400", "Crunchyroll LATAM - Resumen de temporada", "Crunchyroll LATAM", "Frieren")]
    public void EvaluateExternalPost_RejectsUntrustedOrNonClipPosts(
        string id, string duration, string title, string uploader, string subject)
        => Assert.Null(TrailerDownloadService.EvaluateExternalPost(TweetUrl,
            Line(id, duration, title, uploader), subject));

    [Fact]
    public void EvaluateExternalPost_ArticleEmbed_TrustsProvenanceButStillNeedsAVideo()
    {
        // El tweet EMBEBIDO en el artículo: procedencia = relevancia — la
        // cuenta japonesa de la obra twittea en japonés (cero tokens romaji)
        // y con requireTrustSignal=false pasa igual…
        var jp = TrailerDownloadService.EvaluateExternalPost(TweetUrl,
            Line("666", "95", "TVアニメ『ネコと竜』ノンクレジットOP映像公開!", "ネコと竜公式"),
            subject: "The Cat and the Dragon", requireTrustSignal: false);
        Assert.NotNull(jp);

        // …pero un tweet de TEXTO del anuncio (sin video) se rechaza igual
        var textOnly = TrailerDownloadService.EvaluateExternalPost(TweetUrl,
            Line("777", "NA", "アニメ化決定!", "ネコと竜公式"),
            subject: "The Cat and the Dragon", requireTrustSignal: false);
        Assert.Null(textOnly);
    }
}

public class ArticleTweetExtractionTests
{
    [Fact]
    public void ExtractArticleTweetUrl_FindsWordPressTweetEmbed()
    {
        // Embed clásico de WordPress (kudasai): blockquote twitter-tweet con el
        // link al post adentro
        var html = """
            <p>El anuncio se realizó en la cuenta oficial:</p>
            <blockquote class="twitter-tweet"><a href="https://twitter.com/nbuna_staff/status/2077770960586121386?ref_src=twsrc%5Etfw">July 18, 2026</a></blockquote>
            """;

        Assert.Equal("https://x.com/nbuna_staff/status/2077770960586121386",
            AnimeNewsFeedService.ExtractArticleTweetUrl(html));
    }

    [Fact]
    public void ExtractArticleTweetUrl_IgnoresShareButtonsAndProfileLinks()
    {
        // Botón de compartir (intent, sin /status/) + link de perfil del sitio
        var html = """
            <a href="https://twitter.com/intent/tweet?url=https%3A%2F%2Fsomoskudasai.com%2Fnoticia">Compartir</a>
            <a href="https://x.com/somoskudasai">Seguinos en X</a>
            """;

        Assert.Null(AnimeNewsFeedService.ExtractArticleTweetUrl(html));
    }

    [Fact]
    public void ExtractArticleTweetUrl_AcceptsXDomainLinks()
    {
        Assert.Equal("https://x.com/crunchyroll_la/status/2067276084865884314",
            AnimeNewsFeedService.ExtractArticleTweetUrl(
                "<a href=\"https://x.com/crunchyroll_la/status/2067276084865884314\">post</a>"));
    }
}

public class GeminiClientTests
{
    [Fact]
    public void StripCodeFences_UnwrapsGemmaStyleJson()
    {
        // Gemma (el fallback de cuota) no tiene JSON mode nativo: responde el
        // JSON envuelto en un fence markdown que rompería JsonDocument.Parse
        var fenced = "```json\n{\"buscar\": true}\n```";
        Assert.Equal("{\"buscar\": true}", AnimeIndex.Scraper.Infrastructure.AiRewrite.GeminiClient.StripCodeFences(fenced));
    }

    [Fact]
    public void StripCodeFences_LeavesPlainJsonUntouched()
    {
        Assert.Equal("{\"a\":1}", AnimeIndex.Scraper.Infrastructure.AiRewrite.GeminiClient.StripCodeFences("{\"a\":1}"));
        Assert.Equal("{\"a\":1}", AnimeIndex.Scraper.Infrastructure.AiRewrite.GeminiClient.StripCodeFences("  {\"a\":1}\n"));
    }

    [Theory]
    // Casos REALES de prod (16-17 jul-2026): la decisión de video parseaba la
    // respuesta cruda y CUALQUIER envoltura la tiraba a la heurística —
    // Gemma arranca con prosa/markdown ('*' is an invalid start of a value) y
    // hasta flash-lite con JSON mode metió texto después del objeto ('o'/'"'
    // is invalid after a single JSON value).
    [InlineData("* Claro! Acá está el JSON:\n{\"buscar\": true, \"tipo\": \"tema\"}")]
    [InlineData("{\"buscar\": true, \"tipo\": \"tema\"}\nobra: MYTH & ROID")]
    [InlineData("{\"buscar\": true, \"tipo\": \"tema\"}\n\"query\": \"algo suelto\"")]
    [InlineData("```json\n{\"buscar\": true, \"tipo\": \"tema\"}\n```")]
    [InlineData("{\"buscar\": true, \"tipo\": \"tema\"}")]
    public void ExtractJsonObject_RescuesWrappedModelResponses(string raw)
    {
        var json = AnimeIndex.Scraper.Infrastructure.AiRewrite.GeminiClient.ExtractJsonObject(raw);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("buscar").GetBoolean());
        Assert.Equal("tema", doc.RootElement.GetProperty("tipo").GetString());
    }

    [Fact]
    public void ExtractJsonObject_WithoutJsonReturnsTextAsIs()
    {
        // Sin objeto JSON no hay nada que rescatar: el Parse del caller falla
        // y la decisión cae a la heurística (comportamiento correcto)
        Assert.Equal("no puedo ayudarte con eso",
            AnimeIndex.Scraper.Infrastructure.AiRewrite.GeminiClient.ExtractJsonObject("no puedo ayudarte con eso"));
    }

    [Fact]
    public void PickFallbackFromModelList_PrefersLargestInstructionTunedGemma()
    {
        // Google renombra los Gemma entre generaciones (gemma-3-27b-it dio 404
        // en jul-2026): ante un 404 el cliente lista los modelos reales de la
        // key y elige el Gemma -it más grande que soporte generateContent
        var json = """
        {"models":[
          {"name":"models/gemini-2.5-flash-lite","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemma-3n-e4b-it","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemma-4-31b-it","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemma-4-31b","supportedGenerationMethods":["embedContent"]}
        ]}
        """;

        Assert.Equal("gemma-4-31b-it",
            AnimeIndex.Scraper.Infrastructure.AiRewrite.GeminiClient.PickFallbackFromModelList(json));

        // Sin ningún Gemma disponible → null (el 404 original se propaga)
        Assert.Null(AnimeIndex.Scraper.Infrastructure.AiRewrite.GeminiClient.PickFallbackFromModelList(
            """{"models":[{"name":"models/gemini-2.5-flash","supportedGenerationMethods":["generateContent"]}]}"""));
    }
}

public class PhotoQualityGateTests
{
    private static SkiaSharp.SKBitmap Bmp(int w, int h) => new(w, h);

    [Fact]
    public void IsUsablePhoto_AcceptsTypicalOgImageAndRejectsSmallLogos()
    {
        // og:image típico de WordPress (featured image)
        using var ogImage = Bmp(1200, 630);
        Assert.True(AnimeNewsImageService.IsUsablePhoto(ogImage));

        // Logo chico in-body (el caso real: se estiraba a 1080px y quedaba pixelado)
        using var logo = Bmp(300, 200);
        Assert.False(AnimeNewsImageService.IsUsablePhoto(logo));
    }

    [Fact]
    public void IsUsablePhoto_RejectsExtremeBanners()
    {
        // Banner ultra ancho: pasa la resolución mínima pero el aspecto lo delata
        using var banner = Bmp(1920, 500);
        Assert.False(AnimeNewsImageService.IsUsablePhoto(banner));
    }
}

/// <summary>
/// Regresiones del post-mortem del 22-ago-2026: entre el 17 y el 21 de agosto
/// solo 3 de 22 reels salieron con video incrustado. Cada test de acá cubre uno
/// de los cuatro cortes encontrados en los logs de news-cron.
/// </summary>
public class NewsReelVideoRegressionTests
{
    [Fact]
    public void ExtractJsonObject_TakesFirstBalancedObject_GroundedProseRealCase()
    {
        // Forma REAL de la respuesta de Gemini CON GROUNDING (useWebSearch:true)
        // en FindTweetVideoAsync: un plan en markdown, el JSON entre backticks y
        // el esquema repetido más abajo. Con "primer '{' … último '}'" el recorte
        // se comía el backtick y el esquema → JsonReaderException, y la búsqueda
        // del video en X falló en 13 de 13 corridas.
        const string grounded = """
            *   Task: Find an official X post containing a promotional video.
            *   Result: no official post with an uploaded video was found.
            *   Output: `{"url": null}`
            *   Schema reminder: {"url": "https://x.com/<cuenta>/status/<id>"}
            """;

        var json = AnimeIndex.Scraper.Infrastructure.AiRewrite.GeminiClient.ExtractJsonObject(grounded);

        Assert.Equal("""{"url": null}""", json);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.RootElement.GetProperty("url").ValueKind);
    }

    [Fact]
    public void ExtractJsonObject_HandlesNestedObjectsAndBracesInsideStrings()
    {
        // Objeto anidado: el cierre balanceado tiene que ser el ÚLTIMO, no el primero
        var nested = AnimeIndex.Scraper.Infrastructure.AiRewrite.GeminiClient.ExtractJsonObject(
            """Respuesta: {"a": {"b": 1}, "c": 2} — listo.""");
        Assert.Equal("""{"a": {"b": 1}, "c": 2}""", nested);

        // Llaves DENTRO de un string JSON no cuentan como estructura
        var inString = AnimeIndex.Scraper.Infrastructure.AiRewrite.GeminiClient.ExtractJsonObject(
            """prosa {"query": "Solo Leveling {temporada 2}"} fin""");
        using var doc = System.Text.Json.JsonDocument.Parse(inString);
        Assert.Equal("Solo Leveling {temporada 2}", doc.RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public void ExtractJsonObject_StillHandlesFencedAndCleanResponses()
    {
        // Los casos que ya andaban no se rompen
        Assert.Equal("""{"buscar": true}""",
            AnimeIndex.Scraper.Infrastructure.AiRewrite.GeminiClient.ExtractJsonObject(
                "```json\n{\"buscar\": true}\n```"));
        Assert.Equal("""{"buscar": false}""",
            AnimeIndex.Scraper.Infrastructure.AiRewrite.GeminiClient.ExtractJsonObject(
                """{"buscar": false}"""));
    }

    [Fact]
    public void HeuristicVideoQuery_CatchesTheMusicVideoHeadlineTheAiRejected()
    {
        // Caso REAL (run 32196646313, 18-ago-2026): la IA respondió buscar:false
        // para un titular que anuncia un video musical, el reel salió sin video y
        // además se saltearon todos los respaldos. La heurística sí lo detecta —
        // por eso ahora vetea al "no amerita video" de la IA.
        var plan = AnimeNewsPublisherService.HeuristicVideoQuery(
            "Orbitals estrena video musical junto con detalles de vinilo con opening y ending");

        Assert.NotNull(plan);
        Assert.Equal(NewsVideoKind.ThemeSong, plan!.Value.Kind);
        Assert.Contains("Orbitals", plan.Value.Query);
    }

    [Fact]
    public void PickBestSearchResult_UsesTheResultsOwnUrl_NotASynthesizedYouTubeOne()
    {
        // La última red de bilibili sintetizaba "youtube.com/watch?v={id}" con un
        // id de bilibili → URL inexistente. Con el 5º campo (%(url)s) la URL sale
        // del propio resultado.
        var best = TrailerDownloadService.PickBestSearchResult(
            ["BV1xx411c7mD|~|91|~|Cyberpunk Edgerunners 2 PV|~|Netflix|~|https://www.bilibili.com/video/BV1xx411c7mD"],
            requireSpanish: false, subject: "Cyberpunk Edgerunners");

        Assert.Equal("https://www.bilibili.com/video/BV1xx411c7mD", best?.Url);
    }

    [Fact]
    public void PickBestSearchResult_WithoutUrlField_LeavesUrlNullForTheCallerToSynthesize()
    {
        // Las líneas de 4 campos (todo lo previo al 5º) siguen andando
        var best = TrailerDownloadService.PickBestSearchResult(
            ["dR7DW4ykE8k|~|131|~|Solo Leveling en ESPAÑOL | TRÁILER OFICIAL|~|Crunchyroll en Español"]);

        Assert.Equal("dR7DW4ykE8k", best?.Id);
        Assert.Null(best?.Url);
    }

    [Fact]
    public void PickBestSearchResult_RejectsFlatBilibiliResultsWithNoMetadata()
    {
        // Lo que --flat-playlist devuelve en bilibili: id y url, todo lo demás
        // "NA". Sin resolver cada resultado no hay nada que validar — eso dio 0
        // candidatos en 13 de 13 corridas de la última red.
        Assert.Null(TrailerDownloadService.PickBestSearchResult(
        [
            "117130452276756|~|NA|~|NA|~|NA|~|http://www.bilibili.com/video/av117130452276756",
            "116734694595453|~|NA|~|NA|~|NA|~|http://www.bilibili.com/video/av116734694595453",
        ], requireSpanish: false, subject: "Cyberpunk Edgerunners"));
    }

    [Theory]
    // El proxy de WARP y el player_client de YouTube solo valen para YouTube
    [InlineData("https://www.youtube.com/watch?v=SyeHKMfswHk", true)]
    [InlineData("https://youtu.be/SyeHKMfswHk", true)]
    [InlineData("ytsearch", true)]
    [InlineData("bilisearch", false)]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD", false)]
    [InlineData("https://x.com/crunchyroll_la/status/1234567890123", false)]
    [InlineData(null, false)]
    public void IsYouTube_GatesTheWarpProxyAndPlayerClient(string? target, bool expected)
        => Assert.Equal(expected, TrailerDownloadService.IsYouTube(target));
}

/// <summary>
/// Regresiones del segundo post-mortem (22-ago-2026, ya con el fix del PO token
/// en prod): de 5 corridas de reel, 3 salieron con video y las 2 que fallaron
/// fueron por el bot-check de YouTube a nivel IP — una con WARP caído y otra con
/// WARP arriba pero la IP de salida flagueada.
/// </summary>
public class YtDlpCookiesTests
{
    [Fact]
    public void CookiesUsable_OnlyForYouTube_NeverForBilibiliOrX()
    {
        var jar = Path.Combine(Path.GetTempPath(), $"yt-cookies-{Guid.NewGuid():N}.txt");
        File.WriteAllText(jar, "# Netscape HTTP Cookie File\n");
        try
        {
            // YouTube: sí — es donde pega el bot-check
            Assert.True(TrailerDownloadService.CookiesUsable(jar, "https://www.youtube.com/watch?v=SyeHKMfswHk"));
            Assert.True(TrailerDownloadService.CookiesUsable(jar, "https://youtu.be/SyeHKMfswHk"));
            Assert.True(TrailerDownloadService.CookiesUsable(jar, "ytsearch"));

            // Fuera de YouTube: NUNCA. Las cookies de sesión no tienen por qué
            // viajar a bilibili ni a X.
            Assert.False(TrailerDownloadService.CookiesUsable(jar, "bilisearch"));
            Assert.False(TrailerDownloadService.CookiesUsable(jar, "https://www.bilibili.com/video/BV1xx411c7mD"));
            Assert.False(TrailerDownloadService.CookiesUsable(jar, "https://x.com/crunchyroll_la/status/1234567890123"));
            Assert.False(TrailerDownloadService.CookiesUsable(jar, null));
        }
        finally { File.Delete(jar); }
    }

    [Fact]
    public void CookiesUsable_FalseWhenNotConfiguredOrFileMissing()
    {
        const string yt = "https://www.youtube.com/watch?v=SyeHKMfswHk";

        // Sin configurar (el caso de dev local y el de CI sin el secret)
        Assert.False(TrailerDownloadService.CookiesUsable(null, yt));
        Assert.False(TrailerDownloadService.CookiesUsable("", yt));
        Assert.False(TrailerDownloadService.CookiesUsable("   ", yt));

        // Configurado pero el archivo no está: correr igual SIN cookies. Pasarle
        // a yt-dlp un --cookies inexistente lo hace abortar, y eso convertiría un
        // paso best-effort del workflow en una falla dura del reel.
        Assert.False(TrailerDownloadService.CookiesUsable(
            Path.Combine(Path.GetTempPath(), $"no-existe-{Guid.NewGuid():N}.txt"), yt));
    }
}

/// <summary>
/// Regresiones del tercer post-mortem (24-ago-2026). El reel de "Tokyo
/// Revengers: War of the Three Titans Arc revela un nuevo tráiler e imagen
/// promocional" salió SIN video aunque la búsqueda había encontrado 6 tráilers
/// válidos: se probó uno solo, comió el bot-check en sus dos intentos idénticos
/// y los otros 5 nunca se tocaron.
/// </summary>
public class RankedCandidatesTests
{
    // Las 6 líneas REALES que devolvió ytsearch6 para la query de esa corrida.
    private static readonly string[] TokyoRevengersResults =
    [
        "oVpkKSlQ4xg|~|111|~|Tokyo Revengers Season 4 \"War of the Three Titans Arc\" - Official Trailer 3|~|AnimeSelect",
        "di3WrXsDelw|~|54|~|Tokyo Revengers Tercera temporada | Tráiler 2 sub. español|~|IsekTrailers",
        "P-02MjZ27yQ|~|114|~|Tokyo Revengers Season 4 \"War of the Three Titans Arc\" - Official Trailer 2|~|AnimeSelect",
        "N5FVCA6OdCM|~|111|~|Tokyo Revengers Season 4 \"War of the Three Titans Arc\" - Official Trailer|~|AnimeSelect",
        "1pr4908hpCc|~|69|~|tokyo revengers 3 temporada el arco de tenjiku tráiler|~|Multifandom_stay1",
        "nQ2-FdO83ZY|~|135|~|TOKYO REVENGERS TEMPORADA 3 TRAILER #1 SUB ESPAÑOL Y FECHA DE ESTRENO|~|ZONA ANIME",
    ];

    [Fact]
    public void PickRanked_ReturnsEverySuplente_NotJustTheWinner()
    {
        // Sin filtro de idioma (la 2da pasada de la cadena) hay VARIOS tráilers
        // válidos de la obra. Que la lista traiga más de uno es justamente lo
        // que le da red al reel cuando el primero muere por bot-check.
        var ranked = TrailerDownloadService.PickRankedSearchResults(
            TokyoRevengersResults, requireSpanish: false, subject: "Tokyo Revengers Tenjiku Arc");

        Assert.True(ranked.Count > 1,
            $"se esperaban suplentes, vino {ranked.Count}");
        // Todos los devueltos tienen que ser de la obra
        Assert.All(ranked, r => Assert.False(string.IsNullOrWhiteSpace(r.Id)));
        // Y sin repetidos: el caller los prueba en orden
        Assert.Equal(ranked.Count, ranked.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void PickBest_IsStillTheFirstOfTheRankedList()
    {
        // PickBestSearchResult pasó a ser "el primero de PickRankedSearchResults":
        // el comportamiento viejo no cambia, solo se expone el resto.
        var ranked = TrailerDownloadService.PickRankedSearchResults(
            TokyoRevengersResults, requireSpanish: false, subject: "Tokyo Revengers Tenjiku Arc");
        var best = TrailerDownloadService.PickBestSearchResult(
            TokyoRevengersResults, requireSpanish: false, subject: "Tokyo Revengers Tenjiku Arc");

        Assert.Equal(ranked[0].Id, best?.Id);
    }

    [Fact]
    public void PickRanked_EmptyWhenNothingIsTrustworthy()
    {
        // El gate de relevancia sigue mandando: obra equivocada = lista vacía,
        // que es lo que mantiene vivo "mejor slideshow que el video equivocado".
        var ranked = TrailerDownloadService.PickRankedSearchResults(
            TokyoRevengersResults, requireSpanish: false, subject: "Frieren Beyond Journey End");

        Assert.Empty(ranked);
    }

    [Fact]
    public void SubjectFromTitle_DropsGraphicMaterialWords_RealCase()
    {
        // "revela un nuevo tráiler e imagen promocional" mandaba a bilibili la
        // query "...Arc imagen PV" y volvía con longplays de videojuegos.
        var obra = TrailerDownloadService.SubjectFromTitle(
            "Tokyo Revengers: War of the Three Titans Arc revela un nuevo tráiler e imagen promocional");

        Assert.DoesNotContain("imagen", obra, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tokyo", obra);
        Assert.Contains("Revengers", obra);
    }
}

/// <summary>
/// La escalera de idioma del reel: la 2da pasada de búsqueda tiene que dejar de
/// exigir "español latino". El PR #167 la rompió al comparar contra el literal
/// SIN ñ ("espanol latino"), que nunca matchea la query real — la pasada
/// relajada repetía la MISMA query en español y las obras sin doblaje latino
/// quedaban sin video (31-ago → 5-sep-2026: 8 de 20 reels fallidos).
/// </summary>
public class SpanishSuffixStripTests
{
    [Theory]
    // Lo que escriben de verdad el prompt de la IA y HeuristicVideoQuery: CON ñ
    [InlineData("Solo Leveling temporada 2 tráiler oficial español latino",
                "Solo Leveling temporada 2 tráiler oficial")]
    [InlineData("KochiKame: Tokyo Beat Cops tráiler oficial español latino",
                "KochiKame: Tokyo Beat Cops tráiler oficial")]
    // Sin ñ y con mayúsculas: mismo resultado, la comparación es normalizada
    [InlineData("Frieren trailer oficial Espanol Latino", "Frieren trailer oficial")]
    [InlineData("Frieren tráiler oficial ESPAÑOL LATINO", "Frieren tráiler oficial")]
    public void StripSpanishSuffix_RemovesLanguageSuffix_AccentInsensitive(string query, string expected)
        => Assert.Equal(expected, AnimeNewsPublisherService.StripSpanishSuffix(query));

    [Theory]
    // Queries que no llevan el sufijo quedan intactas (temas, cortos, PVs)
    [InlineData("MYTH & ROID Why? RED induction MV")]
    [InlineData("Giant Ojo-sama teaser trailer")]
    [InlineData("Mob Psycho 100 special movie")]
    public void StripSpanishSuffix_LeavesOtherQueriesUntouched(string query)
        => Assert.Equal(query, AnimeNewsPublisherService.StripSpanishSuffix(query));

    [Fact]
    public void StripSpanishSuffix_ActuallyChangesTheAiQuery_RegressionPr167()
    {
        // El caso exacto que salía sin video: las dos pasadas corrían idénticas
        var aiQuery = AnimeNewsPublisherService
            .HeuristicVideoQuery("Frieren confirma su segunda temporada con un tráiler")!.Value.Query;

        Assert.NotEqual(aiQuery, AnimeNewsPublisherService.StripSpanishSuffix(aiQuery));
        Assert.DoesNotContain("español", AnimeNewsPublisherService.StripSpanishSuffix(aiQuery));
        Assert.DoesNotContain("latino", AnimeNewsPublisherService.StripSpanishSuffix(aiQuery));
    }
}

/// <summary>
/// Casos reales que el gate heurístico dejaba pasar como "sin video".
/// </summary>
public class HeuristicVideoQueryAccentTests
{
    [Theory]
    // "live action" SIN guion — el titular real del 3-sep-2026 que salió sin
    // video porque la lista solo tenía la forma guionada
    [InlineData("The Apothecary Diaries dará el salto al live action en 2028")]
    [InlineData("El live-action de One Piece ya tiene fecha de estreno")]
    // Entradas cuya única forma en la lista estaba acentuada: se comparan
    // contra el titular NORMALIZADO, así que el literal con tilde no matcheaba
    // y solo funcionaban por su duplicado sin tilde (ya eliminado)
    [InlineData("Chainsaw Man estrena tráiler de su nueva película")]
    [InlineData("Dandadan confirma la adaptación de su segundo arco")]
    public void HeuristicVideoQuery_DetectsAudiovisualSignal(string title)
    {
        var result = AnimeNewsPublisherService.HeuristicVideoQuery(title);

        Assert.NotNull(result);
        Assert.Equal(NewsVideoKind.Trailer, result!.Value.Kind);
    }
}

/// <summary>
/// Clasificación de errores de publicación de Meta. El 6-sep-2026 el fetcher de
/// Meta falló en 2 de 4 reels y 3 de 3 carruseles mientras las imágenes seguían
/// sirviéndose bien (200, image/jpeg, 1080x1080, ~180 KB): el fallo era
/// transitorio y el código no reintentaba ni una vez.
/// </summary>
public class MetaPublishRetryTests
{
    // El cuerpo EXACTO que devolvió Meta el 6-sep-2026 (run 34052811639).
    // Ojo con "is_transient":false — Meta lo marca permanente y NO lo es.
    private const string MediaDownloadFailureBody = """
        {"error":{"message":"Only photo or video can be accepted as media type.",
        "type":"OAuthException","code":9004,"error_subcode":2207052,"is_transient":false,
        "error_user_title":"Error al descargar el contenido multimedia.",
        "error_user_msg":"No se pudo recuperar el contenido multimedia de este URI",
        "fbtrace_id":"AfulXDO2_i31-9c3c1xIz1x"}}
        """;

    [Fact]
    public void MediaDownloadFailure_IsRetried_DespiteIsTransientFalse()
        => Assert.True(MetaGraphApiClient.IsTransientPublishError(
            System.Net.HttpStatusCode.BadRequest, MediaDownloadFailureBody));

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void ServerErrors_AreRetried(int status)
        => Assert.True(MetaGraphApiClient.IsTransientPublishError(
            (System.Net.HttpStatusCode)status, "{}"));

    [Fact]
    public void MetaDeclaredTransient_IsRetried()
        => Assert.True(MetaGraphApiClient.IsTransientPublishError(
            System.Net.HttpStatusCode.BadRequest,
            """{"error":{"message":"Please retry","code":2,"is_transient":true}}"""));

    [Theory]
    // Token vencido y caption inválido: reintentar solo quema tiempo
    [InlineData("""{"error":{"message":"Error validating access token","code":190,"error_subcode":463}}""")]
    [InlineData("""{"error":{"message":"The caption is too long","code":100,"error_subcode":2207042}}""")]
    // Respuestas que no son JSON no pueden clasificarse como transitorias
    [InlineData("<html>502 Bad Gateway</html>")]
    [InlineData("")]
    public void PermanentErrors_AreNotRetried(string body)
        => Assert.False(MetaGraphApiClient.IsTransientPublishError(
            System.Net.HttpStatusCode.BadRequest, body));
}

/// <summary>
/// Canales oficiales JAPONESES. La lista de distribuidores era solo-latina, así
/// que un canal escrito en katakana no daba señal de "oficial" y el gate
/// relajado (requireSpanish=false) lo descartaba.
/// </summary>
public class OfficialJapaneseChannelTests
{
    [Fact]
    public void EmbeddedOfficialPv_FromKatakanaChannel_IsAccepted_RealCase()
    {
        // Caso exacto del run 34055519423 (6-sep-2026): kudasai embebió el PV
        // oficial y el reel salió igual como slideshow.
        string[] lines =
        [
            "8_Lxr7vO9l0|~|109|~|『転生貴族、鑑定スキルで成り上がる 第3期』PV第2弾【2026年9月27日より放送開始！】|~|isekai channel @バンダイナムコフィルムワークス"
        ];

        // requireSpanish=false es el modo del 2do intento: exige señal de oficial
        var best = TrailerDownloadService.PickBestSearchResult(lines, requireSpanish: false);

        Assert.NotNull(best);
        Assert.Equal("8_Lxr7vO9l0", best!.Value.Id);
    }

    [Theory]
    [InlineData("アニプレックス・チャンネル")]
    [InlineData("東宝MOVIEチャンネル")]
    [InlineData("東映アニメーション公式YouTubeチャンネル")]
    [InlineData("TVアニメ「株式会社マジルミエ」製作委員会")]
    [InlineData("京都アニメーション")]
    public void JapaneseDistributorChannels_CountAsOfficial(string channel)
    {
        string[] lines = [$"abc123xyz|~|95|~|テレビアニメ PV第1弾|~|{channel}"];

        Assert.NotNull(TrailerDownloadService.PickBestSearchResult(lines, requireSpanish: false));
    }

    [Fact]
    public void FanChannel_StillRejected_NoFalsePositives()
    {
        // Sin señal de oficial el gate relajado sigue cerrado: la lista japonesa
        // suma casas reales, no afloja la regla.
        string[] lines = ["abc123xyz|~|95|~|anime trailer 2026|~|AnimeFanEdits"];

        Assert.Null(TrailerDownloadService.PickBestSearchResult(lines, requireSpanish: false));
    }
}

/// <summary>
/// El video EMBEBIDO en el artículo se juzga por PROCEDENCIA: lo eligió la
/// redacción de la fuente para esa noticia. Mismo trato que el tweet embebido.
/// Solo aplica en la pasada relajada; con requireSpanish=true el gate sigue entero.
/// </summary>
public class EmbeddedProvenanceTests
{
    [Fact]
    public void OfficialJapanesePv_Accepted_EvenWithoutRecognizableChannel_RealCase()
    {
        // Caso 6-sep-2026 con el canal FUERA de la lista de distribuidores: ni el
        // canal, ni el título en japonés, ni la palabra del tipo lo salvaban
        // ("PV第2弾" no da frontera para \bpv\b — .NET cuenta los kanji como
        // caracteres de palabra).
        const string line =
            "8_Lxr7vO9l0|~|109|~|『転生貴族、鑑定スキルで成り上がる 第3期』PV第2弾|~|アニメ公式ちゃんねる";

        var c = TrailerDownloadService.EvaluateEmbeddedByProvenance("https://youtu.be/8_Lxr7vO9l0", line);

        Assert.NotNull(c);
        Assert.Equal(109, c!.DurationSeconds);
    }

    [Theory]
    // Los dos filtros que SÍ se mantienen, porque no dependen de reconocer el canal:
    // episodio completo / compilado / live (>6 min) y contenido fan.
    [InlineData("abc123|~|2400|~|Episodio completo|~|canal")]
    [InlineData("abc123|~|3|~|clip cortito|~|canal")]
    [InlineData("abc123|~|120|~|My honest reaction to the new trailer|~|canal")]
    [InlineData("abc123|~|120|~|Reseña y análisis del PV|~|canal")]
    [InlineData("abc123|~|120|~|Naruto AMV 2026|~|canal")]
    public void DurationAndFanContent_StillFilter(string line)
        => Assert.Null(TrailerDownloadService.EvaluateEmbeddedByProvenance("https://youtu.be/abc123", line));

    [Fact]
    public void MalformedLine_ReturnsNull()
        => Assert.Null(TrailerDownloadService.EvaluateEmbeddedByProvenance("https://youtu.be/x", "basura"));
}

/// <summary>
/// Fronteras de palabra contra títulos japoneses. `\b` se apoya en `\w`, que en
/// .NET incluye kanji y kana, así que "PV第2弾" no daba frontera y `\bpv\b`
/// fallaba — justo como titulan los canales oficiales japoneses. Afecta al
/// score de la BÚSQUEDA (kindMatch vale 4 puntos), no solo al embebido.
/// </summary>
public class CjkWordBoundaryTests
{
    // Un canal oficial reconocido aísla la variable: lo único que decide es si
    // la palabra del tipo matchea el título.
    private static string Line(string title) => $"abc123xyz|~|100|~|{title}|~|バンダイナムコフィルムワークス";

    [Theory]
    // Formas japonesas reales: la palabra latina pegada al kanji
    [InlineData("『転生貴族、鑑定スキルで成り上がる 第3期』PV第2弾")]
    [InlineData("第1弾PV【2026年10月放送開始】")]
    [InlineData("アニメ『薬屋のひとりごと』本予告PV")]
    // Y las de siempre, que no deben romperse
    [InlineData("Official Trailer 2026")]
    [InlineData("TEASER")]
    public void KindWord_MatchesAcrossScriptBoundaries(string title)
        => Assert.NotNull(TrailerDownloadService.PickBestSearchResult(
            [Line(title)], requireSpanish: false));

    // Con canal NO oficial y obra verificada, lo ÚNICO que abre el gate relajado
    // es la palabra del tipo (kindMatch && subjectVerified) — así queda aislada.
    private static string[] UnofficialLine(string title) =>
        [$"abc123xyz|~|100|~|{title}|~|RandomUploader"];

    [Fact]
    public void KindWord_GluedToKanji_OpensTheRelaxedGate()
        => Assert.NotNull(TrailerDownloadService.PickBestSearchResult(
            UnofficialLine("アニメ『鬼滅の刃』第2弾PV"), requireSpanish: false, subject: "鬼滅の刃"));

    [Theory]
    // Los falsos positivos que la frontera existía para evitar siguen afuera:
    // "pvc"/"spv" NO son "pv", así que sin palabra del tipo el gate no abre.
    [InlineData("鬼滅の刃 PVC figure unboxing")]
    [InlineData("鬼滅の刃 SPV highlights")]
    public void KindWord_StillRejectsPartialWords(string title)
        => Assert.Null(TrailerDownloadService.PickBestSearchResult(
            UnofficialLine(title), requireSpanish: false, subject: "鬼滅の刃"));

    [Fact]
    public void FanContent_GluedToKanji_IsNowCaught()
        => Assert.Null(TrailerDownloadService.PickBestSearchResult(
            [Line("【感想】my honest reaction【神回】")], requireSpanish: false));
}
