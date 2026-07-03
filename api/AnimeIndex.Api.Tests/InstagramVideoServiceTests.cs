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
    public void Library_EveryMoodHasAtLeastOneTrack()
    {
        foreach (var mood in new[] { "epic", "dark", "upbeat", "chill", "emotional" })
            Assert.Contains(ReelMusicService.Library, t => t.Mood == mood);
    }
}
