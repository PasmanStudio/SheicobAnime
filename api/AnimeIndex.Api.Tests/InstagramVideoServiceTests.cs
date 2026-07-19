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
            "trailer.mp4", "bg.jpg", "overlay.png", [], "out.mp4", 40);

        // El tráiler entra salteando el arranque (logos/negro) CON su audio
        // original — nada de música nuestra ni pista silenciosa
        Assert.Contains("-ss 1.5 -i \"trailer.mp4\"", args);
        Assert.Contains("[1:a]apad,atrim=0:40", args);
        Assert.Contains("-map [v] -map [a]", args);
        Assert.DoesNotContain("anullsrc", args);
        Assert.DoesNotContain("music", args);
        // Fade-out del audio al cierre (40 − 1.8 = 38.2) y nivel social estándar
        Assert.Contains("afade=t=out:st=38.2", args);
        Assert.Contains("loudnorm=I=-16", args);
        // Banda de video capada y congelada si el clip es corto
        Assert.Contains("crop=1080:'min(ih,900)'", args);
        Assert.Contains("tpad=stop_mode=clone", args);
        // Fondo + tráiler + overlay de texto con el slide-up de marca
        Assert.Contains("overlay=x='(W-w)/2':y=240", args);
        Assert.Contains("fade=t=in:st=0.5:d=0.8:alpha=1", args);
        // Specs de Reels intactas
        Assert.Contains("-movflags +faststart", args);
        Assert.Contains("format=yuv420p", args);
        Assert.Contains("-t 40", args);
    }

    [Fact]
    public void BuildTrailerReelArguments_BurnsSpanishSubtitlesWhenProvided()
    {
        var args = InstagramVideoService.BuildTrailerReelArguments(
            "t.mp4", "bg.jpg", "ov.png", [], "out.mp4", 30,
            subtitlesPath: @"C:\temp\subs.vtt");

        // El filtro subtitles va sobre la banda del tráiler, con la ruta
        // escapada para el parser de filtros (C: rompería sin escapar)
        Assert.Contains(@"subtitles='C\:/temp/subs.vtt'", args);
        Assert.Contains("force_style=", args);

        // Sin subs no hay filtro
        var noSubs = InstagramVideoService.BuildTrailerReelArguments(
            "t.mp4", "bg.jpg", "ov.png", [], "out.mp4", 30);
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
            "t.mp4", "bg.jpg", "ov.png", ["kp1.jpg", "cta.jpg"], "out.mp4", 30);

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
        Assert.Contains("[1:a]apad,atrim=0:37", args);
        // Fade-out del audio al final de las slides (37 − 1.8 = 35.2)
        Assert.Contains("afade=t=out:st=35.2", args);
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
