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
}
