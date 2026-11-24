using AnimeIndex.Scraper.Infrastructure.YouTube;

namespace AnimeIndex.Api.Tests;

public class YouTubeVideoTests
{
    [Fact]
    public void MergeSrt_ShiftsEachSceneAndRenumbers()
    {
        // Formato real de edge-tts: cues por frase, pueden solaparse unos ms.
        const string scene1 = "1\r\n00:00:00,100 --> 00:00:03,162\r\nHola.\r\n\r\n2\r\n00:00:03,112 --> 00:00:07,787\r\nSegunda frase.\r\n";
        const string scene2 = "1\n00:00:00,100 --> 00:00:02,500\nOtra escena.\n";

        var merged = VideoRenderService.MergeSrt(
        [
            (scene1, TimeSpan.Zero),
            (scene2, TimeSpan.FromSeconds(8.2)),
        ]);

        Assert.Equal(
            "1\n00:00:00,100 --> 00:00:03,162\nHola.\n\n" +
            "2\n00:00:03,112 --> 00:00:07,787\nSegunda frase.\n\n" +
            "3\n00:00:08,300 --> 00:00:10,700\nOtra escena.\n\n",
            merged);
    }

    [Fact]
    public void EstimateSrt_SplitsBySentenceAndSpreadsByLength()
    {
        var srt = VideoRenderService.EstimateSrt("Esta empieza justo ahí. ¿Y ahora qué?", 4.0);

        // 23 + 13 letras sobre 4 s → la primera frase ocupa 23/36 del tiempo.
        Assert.Equal(
            "1\n00:00:00,000 --> 00:00:02,555\nEsta empieza justo ahí.\n\n" +
            "2\n00:00:02,555 --> 00:00:04,000\n¿Y ahora qué?\n\n",
            srt);
    }

    [Fact]
    public void EstimateSrt_CutsLongSentencesAtCommas()
    {
        const string longSentence =
            "En primer lugar, ya hemos visto a la bruja de la Vanagloria hacer algo similar, " +
            "cuando utilizó su autoridad para modificar la realidad del bosque entero.";

        var cues = VideoRenderService.EstimateSrt(longSentence, 10).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.True(cues.Length >= 2);
        Assert.All(cues, c => Assert.True(c.Split('\n')[2].Length <= 91));
    }

    [Fact]
    public void VideoPlan_Parse_DropsScenesWithoutNarration()
    {
        const string json = """
            { "title": "T", "scenes": [
                { "narration": "Uno", "visual": "keyart" },
                { "narration": "  ", "visual": "ai", "imagePrompt": "x" },
                { "Narration": "Dos", "visual": "ai", "imagePrompt": "a road" } ] }
            """;

        var plan = VideoPlan.Parse(json);

        Assert.Equal(["Uno", "Dos"], plan.Scenes.Select(s => s.Narration));
        Assert.Equal("a road", plan.Scenes[1].ImagePrompt);
    }

    [Fact]
    public void VideoPlan_Parse_RejectsPlanWithoutScenes() =>
        Assert.Throws<InvalidOperationException>(() => VideoPlan.Parse("""{ "title": "T", "scenes": [] }"""));

    [Fact]
    public void StripHtml_KeepsTextAndDropsScripts()
    {
        const string html = "<html><script>var x = 1;</script><p>Frieren &amp; Himmel</p><p>Segundo<br>renglón</p></html>";

        var text = VideoResearchService.StripHtml(html);

        Assert.Equal("Frieren & Himmel\nSegundo\nrenglón", text);
    }

    [Fact]
    public void ImageShotArgs_ZoompanMakesExactlyTheShotFrames()
    {
        var args = VideoRenderService.ImageShotArgs("frame.jpg", 105, 0, flash: false, "shot.mp4");

        var filter = args[args.IndexOf("-vf") + 1];
        Assert.Contains("d=105:", filter);
        Assert.Equal("105", args[args.IndexOf("-frames:v") + 1]);
        Assert.Contains("-an", args); // la voz va en una pista aparte
        // Una sola imagen de entrada: con -loop 1 zoompan multiplicaría los cuadros.
        Assert.DoesNotContain("-loop", args);
        Assert.DoesNotContain("color=white", filter);
    }

    [Fact]
    public void ImageShotArgs_FlashFadesInFromWhite() =>
        Assert.Contains("fade=t=in:st=0:d=0.1:color=white",
            VideoRenderService.ImageShotArgs("f.jpg", 90, 3, flash: true, "s.mp4") is var a ? a[a.IndexOf("-vf") + 1] : "");

    [Fact]
    public void PlanShots_SplitsScenesIntoEvenShotsWithoutGaps()
    {
        // 10,5 s → 3 tomas de 3,5 s; 2 s → 1 toma (nunca 0); 7,2 s → 2 de 3,6 s.
        var shots = VideoRenderService.PlanShots([10.5, 2.0, 7.2], 3.5);

        Assert.Equal([3, 1, 2], shots.GroupBy(s => s.Scene).Select(g => g.Count()));
        for (var i = 1; i < shots.Count; i++)
            Assert.Equal(shots[i - 1].Start + shots[i - 1].Duration, shots[i].Start, 6);
        Assert.Equal(19.7, shots[^1].Start + shots[^1].Duration, 6);
        Assert.All(shots.Where(s => s.Slot == 0), s => Assert.Equal(0, s.Slot));
    }

    [Fact]
    public void ChooseShotSource_FirstShotIsTheSceneImageThenRotatesTrailer()
    {
        var shots = VideoRenderService.PlanShots([14.0], 3.5); // 4 tomas
        var withTrailer = shots.Select((s, i) => VideoRenderService.ChooseShotSource(s, i, clips: 10, stills: 10)).ToList();
        var withoutTrailer = shots.Select((s, i) => VideoRenderService.ChooseShotSource(s, i, clips: 0, stills: 0)).ToList();

        Assert.Equal([ShotSource.SceneImage, ShotSource.TrailerClip, ShotSource.TrailerStill, ShotSource.TrailerClip], withTrailer);
        Assert.All(withoutTrailer, s => Assert.Equal(ShotSource.SceneImage, s));
    }

    [Fact]
    public void ParseMetaTimestamp_AcceptsOffsetWithoutColon() =>
        // Así lo manda la Graph API; GetDateTime() de System.Text.Json lo rechazaba (corrida 45493953065).
        Assert.Equal(new DateTime(2026, 11, 4, 20, 30, 0, DateTimeKind.Utc),
            AnimeIndex.Scraper.Infrastructure.Instagram.MetaGraphApiClient.ParseMetaTimestamp("2026-11-04T20:30:00+0000"));

    [Fact]
    public void WikiSlugCandidates_CoversTheUsualFandomNames()
    {
        var slugs = VisualSearchService.WikiSlugCandidates(["Cyberpunk: Edgerunners", "Sousou no Frieren"]);

        // cyberpunk.fandom.com y frieren.fandom.com son las wikis reales (24-nov-2026).
        Assert.Contains("cyberpunk", slugs);
        Assert.Contains("cyberpunkedgerunners", slugs);
        Assert.Contains("frieren", slugs);
        Assert.DoesNotContain("no", slugs);
    }

    [Theory]
    [InlineData("File:David Lucy Moon EP2 CPEDGE.jpg", "image/jpeg", 1920, 1080, true)]
    [InlineData("File:Lucy 1-6 Scale Statue.jpg", "image/jpeg", 1920, 1080, false)]
    [InlineData("File:Aura with her Scales of Obedience CH18.png", "image/png", 1249, 448, false)]
    [InlineData("File:Lucy portrait.png", "image/png", 600, 900, false)]
    public void IsUsableScene_KeepsEpisodeStillsOnly(string title, string mime, int w, int h, bool expected) =>
        Assert.Equal(expected, VisualSearchService.IsUsableScene(title, mime, w, h));

    [Fact]
    public void PickAwayFrom_SkipsTrailerMomentsTooCloseToTheLastOne()
    {
        double[] times = [10, 12, 15, 30, 31, 60];
        var turn = 0;

        Assert.Equal(0, VideoRenderService.PickAwayFrom(times, ref turn, last: null));   // 10 s
        Assert.Equal(3, VideoRenderService.PickAwayFrom(times, ref turn, last: 10));     // salta 12 y 15
        Assert.Equal(5, VideoRenderService.PickAwayFrom(times, ref turn, last: 30));     // salta 31
        var empty = 0;
        Assert.Equal(-1, VideoRenderService.PickAwayFrom([], ref empty, last: null));
    }

    [Fact]
    public void VoiceTrackArgs_PadsEachSceneAndConcatsInOrder()
    {
        var args = VideoRenderService.VoiceTrackArgs(["v1.wav", "v2.wav"], [0.2, 1.0], "voice.wav");

        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.Equal(
            "[0:a]apad=pad_dur=0.2[a0];[1:a]apad=pad_dur=1[a1];[a0][a1]concat=n=2:v=0:a=1[out]", filter);
        Assert.Equal("voice.wav", args[^1]);
    }
}
