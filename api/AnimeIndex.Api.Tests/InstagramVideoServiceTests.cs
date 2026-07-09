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
    public void BuildTrailerReelArguments_MutesTrailerAndOverlaysBrandText()
    {
        var args = InstagramVideoService.BuildTrailerReelArguments(
            "trailer.mp4", "bg.jpg", "overlay.png", "out.mp4", 18);

        // El tráiler entra salteando el arranque (logos/negro) y NUNCA su audio:
        // el único mapeo de audio es la pista propia (input 3)
        Assert.Contains("-ss 1.5 -i \"trailer.mp4\"", args);
        Assert.DoesNotContain("[1:a]", args);
        Assert.Contains("-map 3:a", args);
        // Banda de video capada y congelada si el clip es corto
        Assert.Contains("crop=1080:'min(ih,900)'", args);
        Assert.Contains("tpad=stop_mode=clone", args);
        // Fondo + tráiler + overlay de texto con el slide-up de marca
        Assert.Contains("overlay=x='(W-w)/2':y=240", args);
        Assert.Contains("fade=t=in:st=0.5:d=0.8:alpha=1", args);
        // Specs de Reels intactas
        Assert.Contains("-movflags +faststart", args);
        Assert.Contains("format=yuv420p", args);
        Assert.Contains("-t 18", args);
    }

    [Fact]
    public void BuildTrailerReelArguments_WithMusic_MapsOwnTrackOnly()
    {
        var args = InstagramVideoService.BuildTrailerReelArguments(
            "t.mp4", "bg.jpg", "ov.png", "out.mp4", 18, musicPath: "music.mp3");

        Assert.DoesNotContain("anullsrc", args);
        Assert.Contains("[3:a]afade", args);
        Assert.Contains("-map [a]", args);
        // Fade-out en 18 − 1.5 = 16.5
        Assert.Contains("afade=t=out:st=16.5", args);
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
    public void PickBestSearchResult_PrefersOfficialTrailerOverFanContent()
    {
        var best = TrailerDownloadService.PickBestSearchResult(
        [
            Line("fanvid00001", "600", "Sword Art Online explicado en 10 minutos", "OtakuFan"),
            Line("official0001", "95", "Sword Art Online Integral Domain Official Trailer", "Aniplex"),
            Line("reaction001", "120", "REACCIÓN al trailer de Sword Art Online", "ReactBro"),
        ]);

        Assert.Equal("official0001", best);
    }

    [Fact]
    public void PickBestSearchResult_RejectsLongVideosEvenWithTrailerInTitle()
    {
        // >6 min no es un tráiler (episodio/compilado/live), aunque el título diga trailer
        Assert.Null(TrailerDownloadService.PickBestSearchResult(
            [Line("longvideo001", "1800", "Todos los trailers de anime 2028", "Recopilador")]));
    }

    [Fact]
    public void PickBestSearchResult_RequiresTrailerSignalInTitle()
    {
        // Sin señal de tráiler en el título no hay confianza — mejor slideshow
        // que incrustar el video equivocado
        Assert.Null(TrailerDownloadService.PickBestSearchResult(
            [Line("randomvid001", "90", "Sword Art Online opening full", "MusicChannel")]));
        Assert.Null(TrailerDownloadService.PickBestSearchResult([]));
    }

    [Fact]
    public void PickBestSearchResult_RealWorldSearch_PicksOfficialUploadWithoutTrailerWord()
    {
        // Resultados REALES de "ytsearch6:Sword Art Online Integral Domain official
        // trailer" (jul-2026). El teaser oficial de Aniplex NO dice "trailer" en el
        // título — el bonus por canal oficial + el orden de relevancia lo rescatan
        // frente al re-upload y al trailer fan-made.
        var best = TrailerDownloadService.PickBestSearchResult(
        [
            Line("UHWMxtRivt8", "17", "Sword Art Online the Movie - Integral Domain -  |  COMING 2028", "Aniplex USA"),
            Line("FwKFcfPK4m8", "238", "SWORD ART ONLINE : INTEGRAL DOMAIN TO RELEASE IN 2028", "Adam's Anime World"),
            Line("a2_XZColIY4", "17", "Sword Art Online Integral Domain Anime Movie - Official Teaser", "Anime Officials Trailer"),
            Line("jcsFEbam_aY", "16", "Sword Art Online the Movie: Integral Domain Official Trailer #anime", "KingYan Animation Studio"),
            Line("tYiugIxhRLA", "484", "EVERYTHING about the NEW SAO MOVIE | Integral Domain", "iFedeLima YT"),
            Line("Q0C91P7q5iM", "97", "Sword Art Online New Movie Integral Domain Release Date & Latest Update", "ANIKINGZ"),
        ]);

        Assert.Equal("UHWMxtRivt8", best);
    }

    [Fact]
    public void PickBestSearchResult_AcceptsJapanesePvMarkers()
    {
        var best = TrailerDownloadService.PickBestSearchResult(
        [
            Line("japanesepv01", "NA", "TVアニメ『葬送のフリーレン』本予告", "TOHO animation チャンネル"),
        ]);

        Assert.Equal("japanesepv01", best);
    }

    [Theory]
    // Titulares que anuncian material audiovisual → buscar
    [InlineData("Sword Art Online anuncia nueva película para 2028", true)]
    [InlineData("Frieren confirma su segunda temporada con un tráiler", true)]
    [InlineData("El live-action de One Piece ya tiene fecha de estreno", true)]
    // Titulares sin video que buscar → null (queda el slideshow)
    [InlineData("Sword Art Online anuncia una nueva novela sobre Kirito y Asuna", false)]
    [InlineData("Fallece reconocido animador del estudio Ghibli", false)]
    [InlineData("Nueva figura coleccionable de Nezuko agota su preventa", false)]
    public void HeuristicTrailerQuery_OnlyTriggersOnAudiovisualNews(string title, bool expectsQuery)
    {
        var query = AnimeNewsPublisherService.HeuristicTrailerQuery(title);

        if (expectsQuery)
        {
            Assert.NotNull(query);
            Assert.Contains("trailer", query);
            Assert.Contains(title, query);   // el nombre de la obra viaja en el titular
        }
        else
        {
            Assert.Null(query);
        }
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
