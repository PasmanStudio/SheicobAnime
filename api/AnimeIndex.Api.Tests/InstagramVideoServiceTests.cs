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
        => Assert.All(ReelMusicService.Library,
            t => Assert.Contains("CC BY 4.0", t.Attribution));

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
        // Track propio → sello de marca, no CC
        Assert.Equal(ReelMusicService.OwnMusicAttribution, track.Attribution);
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
