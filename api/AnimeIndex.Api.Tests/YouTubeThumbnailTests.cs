using AnimeIndex.Scraper.Infrastructure.YouTube;
using SkiaSharp;

namespace AnimeIndex.Api.Tests;

public class YouTubeThumbnailTests
{
    private static VideoPlan PlanWith(params string[] shows) => new()
    {
        Title = "El SECRETO de Weak Kingsley en Cyberpunk: Edgerunners 2",
        Scenes = [new VideoScene { Narration = "x", Shots = shows.Select(s => new VideoShotSpec { At = "x", Show = s }).ToList() }],
    };

    [Fact]
    public void Planner_UsesThePlanHookAndMakesOneChangePerVariant()
    {
        var plan = PlanWith();
        plan.Thumbnail = new VideoThumbnail { Text = "¿AMOR O *TRAICIÓN*?", Alternative = "ANTES DE *TODO*", Characters = ["Weak Kingsley", "Avida Yang"] };

        var v = ThumbnailPlanner.Variants(plan);

        Assert.Equal(["thumbnail.jpg", "thumbnail_b.jpg", "thumbnail_c.jpg"], v.Select(x => x.File));
        Assert.All(v, x => Assert.Equal(["Weak Kingsley", "Avida Yang"], x.Characters));
        Assert.Equal("¿AMOR O *TRAICIÓN*?", v[0].Text);
        Assert.Equal("ANTES DE *TODO*", v[1].Text);
        Assert.Null(v[2].Text);
    }

    [Fact]
    public void Planner_OldPlans_UseMostNamedCharactersAndTheTitleCapsWord()
    {
        var plan = PlanWith("character: Avida Yang", "character: Weak Kingsley", "poster: X", "character: Weak Kingsley", "character: Lucy");

        Assert.Equal(["Weak Kingsley", "Avida Yang"], ThumbnailPlanner.Characters(plan));
        Assert.Equal("*SECRETO*", ThumbnailPlanner.FallbackText(plan.Title));
        Assert.Null(ThumbnailPlanner.FallbackText("Todo sobre Frieren"));
        Assert.Equal("UNO DOS TRES CUATRO", ThumbnailPlanner.Clean("UNO DOS TRES CUATRO CINCO"));
    }

    [Fact]
    public void CoverSource_FillsTheFrameWithoutBands()
    {
        // Banner ultraancho de AniList (1900x400) → 16:9: se recorta el ancho, nunca franjas.
        var src = ThumbnailRenderer.CoverSource(1900, 400, 1280, 720);
        Assert.Equal(400, src.Height, 1);
        Assert.Equal(400 * 1280f / 720, src.Width, 1);
        Assert.Equal((1900 - src.Width) / 2, src.Left, 1);
    }

    [Fact]
    public void Layout_SplitsIntoBalancedLinesAndHighlightsTheMarkedWord()
    {
        var (lines, count) = ThumbnailRenderer.Layout("¿amor o *traición*?");
        Assert.Equal(3, count);
        Assert.Contains(lines.SelectMany(l => l), w => w.Word == "TRAICIÓN?" && w.Highlight);
        Assert.DoesNotContain(lines.SelectMany(l => l), w => w.Word.Contains('*'));

        var (single, _) = ThumbnailRenderer.Layout("nadie lo vio");
        Assert.True(single.Last().Last().Highlight); // sin asteriscos: la última
    }

    [Fact]
    public void SubjectRects_KeepCharactersOnTheRightWhenThereIsText()
    {
        var rects = ThumbnailRenderer.SubjectRects([(460, 650), (460, 650)], hasText: true);
        Assert.Equal(2, rects.Count);
        Assert.All(rects, r => Assert.True(r.MidX > ThumbnailRenderer.W * 0.5f));
        Assert.All(rects, r => Assert.True(r.Bottom >= ThumbnailRenderer.H)); // cortados abajo, plano medio
    }

    [Fact]
    public void EdgeFade_HidesStraightCutsAtTheImageBorders()
    {
        Assert.Equal(1, AnimeCutout.EdgeFade(50, 50, 100, 100), 3);
        Assert.Equal(0, AnimeCutout.EdgeFade(0, 50, 100, 100), 3);
        Assert.Equal(0, AnimeCutout.EdgeFade(50, 99, 100, 100), 3);
        Assert.InRange(AnimeCutout.EdgeFade(50, 88, 100, 100), 0.01f, 0.9f);
    }

    [Fact]
    public void Render_TextIsReadableInTheFeedAndAvoidsTheDurationBadge()
    {
        var path = Path.Combine(Path.GetTempPath(), $"thumb_{Guid.NewGuid():N}.jpg");
        using var subject = new SKBitmap(460, 650);
        subject.Erase(SKColors.Orange);
        try
        {
            var report = ThumbnailRenderer.Render(new ThumbnailSpec(null, [new ThumbnailSubject(subject, false)], "¿AMOR O *TRAICIÓN*?"), path);

            Assert.True(report.Ok, string.Join("; ", report.Warnings));
            Assert.True(report.FeedCapHeight >= ThumbnailRenderer.MinFeedCapHeight);
            Assert.False(report.TextBounds.IntersectsWith(ThumbnailRenderer.DurationBadge));
            using var written = SKBitmap.Decode(path);
            Assert.Equal((1280, 720), (written.Width, written.Height));
        }
        finally { File.Delete(path); }
    }
}
