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
    public void SegmentArgs_ZoompanCoversWholeSceneAtThirtyFps()
    {
        var args = VideoRenderService.SegmentArgs("frame.jpg", "voz.mp3", 8.4, 0, "seg.mp4");

        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("d=252:", filter); // ceil(8.4 s * 30 fps)
        Assert.Equal("8.4", args[args.IndexOf("-t") + 1]);
        // Una sola imagen de entrada: con -loop 1 zoompan multiplicaría los cuadros.
        Assert.DoesNotContain("-loop", args);
    }
}
