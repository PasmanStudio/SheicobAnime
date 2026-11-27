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
    public void VideoPlan_Parse_DropsLikeAndSubscribeFromTheClosing()
    {
        // El cierre real de cyberpunk-full (26-nov-2026), más una escena del medio
        // que dice "me gusta" y no se toca: solo se limpian las últimas escenas.
        const string json = """
            { "title": "T", "scenes": [
                { "narration": "A los fans les dijo: me gusta este final.", "visual": "ai" },
                { "narration": "¿Crees que esto cambia tu perspectiva sobre Weak?", "visual": "keyart" },
                { "narration": "Déjanos tus comentarios aquí abajo, queremos leer tu opinión. Y si te ha gustado este análisis, no olvides darle un buen 'me gusta' y suscribirte a Sheicob Anime para no perderte ningún video.", "visual": "keyart" } ] }
            """;

        var plan = VideoPlan.Parse(json);

        Assert.Equal(
        [
            "A los fans les dijo: me gusta este final.",
            "¿Crees que esto cambia tu perspectiva sobre Weak?",
            "Déjanos tus comentarios aquí abajo, queremos leer tu opinión.",
        ], plan.Scenes.Select(s => s.Narration));
    }

    [Fact]
    public void VideoPlan_Parse_DropsAClosingSceneThatOnlyAsksToSubscribe()
    {
        const string json = """
            { "title": "T", "scenes": [
                { "narration": "¿Qué opinas tú?", "visual": "ai" },
                { "narration": "Suscríbete y activa la campanita.", "visual": "keyart" } ] }
            """;

        Assert.Equal(["¿Qué opinas tú?"], VideoPlan.Parse(json).Scenes.Select(s => s.Narration));
    }

    [Fact]
    public void EndScreenArgs_StillBlurredImageWithExactFrames()
    {
        var args = VideoRenderService.EndScreenArgs("frame_01.jpg", 450, "shot_end.mp4");

        var filter = args[args.IndexOf("-vf") + 1];
        Assert.Equal("450", args[args.IndexOf("-frames:v") + 1]);
        Assert.Contains("gblur", filter);
        Assert.DoesNotContain("zoompan", filter); // nada se mueve debajo de los elementos
        Assert.Contains("-an", args);
        Assert.Equal(450, (int)(VideoRenderService.EndScreenSeconds * 30));
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

    // Respuestas esperadas según la investigación de fonología del 26-nov-2026.
    [Theory]
    [InlineData("Shingeki no Kyojin", "Shingueki no Kioyin")]
    [InlineData("Jujutsu Kaisen", "Dyudyutsu Kaisen")] // elegido de oído (A/B 26-nov)
    [InlineData("Gojo Satoru", "Goyo Satoru")]
    [InlineData("Gojō", "Goyo")]
    [InlineData("Ryomen Sukuna", "Riomen Sukuna")]
    [InlineData("Kimetsu no Yaiba", "Kimetsu no Yaiba")]
    [InlineData("Tanjiro", "Tanjiro")] // elegido de oído: suena mejor que "Tanyiro"
    [InlineData("Kamado Tanjiro", "Kamado Tanjiro")]
    [InlineData("Junji Ito", "Yunyi Ito")] // fuera de las excepciones, la regla j → y sigue
    [InlineData("Hashira", "Jashira")]
    [InlineData("Uchiha Sasuke", "Uchija Sasuke")]
    [InlineData("Shippuden", "Shipuden")]
    [InlineData("Sousou no Frieren", "Soso no Frieren")]
    [InlineData("Kaguya-sama", "Kaguya sama")]
    [InlineData("Denji", "Denyi")]
    [InlineData("Gyomei", "Guiomei")]
    [InlineData("Kenpachi", "Kempachi")]
    [InlineData("Yuuji", "Yuyi")]
    [InlineData("Shin'ya", "Shinya")]
    [InlineData("sensei", "sensei")] // sin tildes automáticas: "senséi" lo pone el glosario
    public void JapaneseRespeller_FollowsSpanishPhoneticRules(string romaji, string expected) =>
        Assert.Equal(expected, JapaneseRespeller.Respell(romaji));

    [Fact]
    public void PronunciationsFrom_RespellsJapaneseByRulesAndKeepsGeminiForEnglish()
    {
        const string board = """
            { "foreignTerms": [
                { "term": "Shingeki no Kyojin", "lang": "ja", "say": "Shinguéki no Kioyin" },
                { "term": "Weak Kingsley", "lang": "en", "say": "Uik Kingsli" },
                { "term": "Kaiju", "lang": "ja" },
                { "term": "Netflix", "lang": "en", "say": "Netflix" } ] }
            """;

        var map = VideoScriptService.PronunciationsFrom(board);

        Assert.Equal("Shingueki no Kioyin", map["Shingeki no Kyojin"]); // ignora el "say" de Gemini para japonés
        Assert.Equal("Uik Kingsli", map["Weak Kingsley"]);
        Assert.Equal("Kaiyu", map["Kaiju"]);
        Assert.False(map.ContainsKey("Netflix")); // igual al original: no hace falta
        // Pero el audio de control los lista a todos, también los que no cambian.
        Assert.Equal(["Shingeki no Kyojin", "Weak Kingsley", "Kaiju", "Netflix"], VideoScriptService.ForeignTermsFrom(board));
    }

    [Fact]
    public void VideoPlan_Parse_AcceptsTheStoryboardWithForeignTermObjects()
    {
        // El storyboard entero se parsea como VideoPlan (corrida 45853115053 se cayó acá).
        const string board = """
            { "title": "T", "mood": "dark",
              "foreignTerms": [ { "term": "Night City", "lang": "en", "say": "Nait Siti" } ],
              "scenes": [ { "narration": "En Night City.", "visual": "keyart" } ] }
            """;

        var plan = VideoPlan.Parse(board);

        Assert.Equal("dark", plan.Mood);
        Assert.Single(plan.Scenes);
    }

    [Fact]
    public void ForSpeech_RespellsWholeTermsLongestFirst()
    {
        var map = new Dictionary<string, string>
        {
            ["Jujutsu"] = "Yuyutsu",
            ["Jujutsu Kaisen"] = "Yuyutsu Kaisen",
            ["Gojo"] = "Goyo",
            ["_comentario"] = "ignorado",
        };

        var speech = VideoPlan.ForSpeech("En Jujutsu Kaisen, Gojo usa el Jujutsu. Gojou y Gojos no cambian.", map);

        Assert.Equal("En Yuyutsu Kaisen, Goyo usa el Yuyutsu. Gojou y Gojos no cambian.", speech);
    }

    [Fact]
    public void MergeGlossary_WinsInsideLongerGeminiTerms()
    {
        var plan = new VideoPlan
        {
            Pronunciations = new()
            {
                ["Cyberpunk: Edgerunners"] = "Cáiberpunk Édyerruuners", // Gemini, contiene "Edgerunners"
                ["Night City"] = "Nait Siti",
            },
        };

        plan.MergeGlossary(new Dictionary<string, string> { ["Edgerunners"] = "Édchraners", ["_nota"] = "x" });

        Assert.False(plan.Pronunciations.ContainsKey("Cyberpunk: Edgerunners"));
        Assert.Equal("Nait Siti", plan.Pronunciations["Night City"]);
        Assert.Equal("Cyberpunk: Édchraners en Nait Siti",
            VideoPlan.ForSpeech("Cyberpunk: Edgerunners en Night City", plan.Pronunciations));
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
    public void PickTracks_SameMoodDistinctAndStablePerVideo()
    {
        var a = VideoMusic.PickTracks("dark", "Cyberpunk: la teoría", 3);
        var again = VideoMusic.PickTracks("dark", "Cyberpunk: la teoría", 3);

        Assert.Equal(3, a.Select(t => t.Title).Distinct().Count());
        Assert.All(a, t => Assert.Equal("dark", t.Mood));
        Assert.Equal(a.Select(t => t.Title), again.Select(t => t.Title));
        Assert.All(VideoMusic.PickTracks(null, "x", 2), t => Assert.Equal("dark", t.Mood)); // sin mood → dark
        Assert.All(a, t => Assert.Contains("CC BY", t.Attribution));
    }

    [Fact]
    public void MusicBedArgs_CrossfadesTracksIntoOneOutput()
    {
        var args = VideoMusic.MusicBedArgs([("a.mp3", 5), ("b.mp3", 0), ("c.mp3", 0)], "music.wav");

        Assert.Equal("5", args[args.IndexOf("-ss") + 1]); // salta la intro del primero
        Assert.Equal(1, args.Count(a => a == "-ss"));
        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("[t0][t1]acrossfade=d=3[x1];[x1][t2]acrossfade=d=3[out]", filter);

        var single = VideoMusic.MusicBedArgs([("a.mp3", 0)], "music.wav");
        Assert.Equal("[0:a]aformat=sample_rates=48000:channel_layouts=stereo[out]", single[single.IndexOf("-filter_complex") + 1]);
    }

    [Fact]
    public void FinalMuxArgs_OnlyEncodesTheMixedAudio()
    {
        var args = VideoRenderService.FinalMuxArgs("mix_norm.wav", 300);

        Assert.Equal("mix_norm.wav", args[args.IndexOf("-i", args.IndexOf("-i") + 1) + 1]);
        Assert.Equal("1:a", args[args.LastIndexOf("-map") + 1]);
        // Sin filtros de audio en el mux: ahí el ffmpeg 6.1 del runner comía voz.
        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.DoesNotContain("amix", filter);
        Assert.DoesNotContain("-stream_loop", args);
        Assert.Equal("300", args[args.IndexOf("-t") + 1]);
    }

    [Fact]
    public void AudioMix_Join_KeepsEverySampleAndAddsTheGaps()
    {
        var a = new PcmAudio(24000, 1, [1, 2, 3]);
        var b = new PcmAudio(24000, 1, [4, 5]);
        var joined = AudioMix.Join([a, b], [1.0 / 24000, 2.0 / 24000]);

        Assert.Equal(new short[] { 1, 2, 3, 0, 4, 5, 0, 0 }, joined.Samples);
        Assert.Throws<InvalidOperationException>(() => AudioMix.Join([a, new PcmAudio(48000, 1, [1])], [0, 0]));
    }

    [Fact]
    public void AudioMix_Mix_DucksMusicWhileTheVoiceSpeaks()
    {
        const int rate = 1000;
        var frames = 10 * rate;
        // Voz: silencio 0–4 s, habla 4–6 s, silencio 6–10 s. Música constante.
        var voice = new short[frames];
        for (var i = 4 * rate; i < 6 * rate; i++) voice[i] = (short)(i % 2 == 0 ? 8000 : -8000);
        var music = Enumerable.Repeat((short)10000, frames).ToArray();

        var mixed = AudioMix.Mix(new PcmAudio(rate, 1, voice), new PcmAudio(rate, 1, music), 0.5);

        Assert.Equal(frames, mixed.Frames);
        var quiet = mixed.Samples[3 * rate];          // sin voz, después del fundido de entrada
        var under = mixed.Samples[6 * rate - 1] - voice[6 * rate - 1]; // música debajo de la voz
        Assert.Equal(5000, quiet);
        Assert.InRange(under, 1, 3000);               // bajó (ducking)
        Assert.Equal(0, mixed.Samples[^1]);           // fundido de salida
        Assert.Same(voice, AudioMix.Mix(new PcmAudio(rate, 1, voice), null, 0.5).Samples);
    }

    [Fact]
    public void PcmAudio_RoundTripsThroughWav()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pcm_{Guid.NewGuid():N}.wav");
        try
        {
            new PcmAudio(48000, 2, [1, -1, 300, -300]).Write(path);
            var read = PcmAudio.Read(path);
            Assert.Equal((48000, 2), (read.SampleRate, read.Channels));
            Assert.Equal(new short[] { 1, -1, 300, -300 }, read.Samples);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AudioMix_PtsGaps_FindsMissingAudio()
    {
        // Paquetes AAC de 1024 muestras a 48 kHz (21,33 ms); falta un tramo de 0,88 s.
        var pts = Enumerable.Range(0, 50).Select(i => i * 0.021333).ToList();
        pts.AddRange(Enumerable.Range(0, 50).Select(i => 50 * 0.021333 + 0.88 + i * 0.021333));

        var gaps = AudioMix.PtsGaps(pts);

        Assert.Single(gaps);
        Assert.Equal(0.88 + 0.021333, gaps[0].To - gaps[0].From, 3);
        Assert.Empty(AudioMix.PtsGaps(pts.Take(50).ToList()));
        Assert.Equal([0.5, 1.25], AudioMix.ParsePts("0.500000\n1.250000\n\n"));
    }

    [Fact]
    public void AudioMix_Loudness_TwoPassesInLinearMode()
    {
        const string stderr = "... [Parsed_loudnorm_0 @ 0x1]\n{\n\t\"input_i\" : \"-22.10\",\n\t\"input_tp\" : \"-1.60\",\n" +
                              "\t\"input_lra\" : \"4.10\",\n\t\"input_thresh\" : \"-32.20\",\n\t\"target_offset\" : \"0.30\"\n}\n";
        var measured = AudioMix.ParseLoudness(stderr);
        var args = AudioMix.LoudnessApplyArgs("mix.wav", "out.wav", measured);
        var filter = args[args.IndexOf("-af") + 1];

        Assert.Contains("I=-14", filter);
        Assert.Contains("measured_I=-22.10:measured_TP=-1.60:measured_LRA=4.10:measured_thresh=-32.20:offset=0.30:linear=true", filter);
        Assert.Contains("print_format=json", AudioMix.LoudnessMeasureArgs("mix.wav")[5]);
    }

    [Fact]
    public void TrimVoiceArgs_KeepsNaturalEdgesInsteadOfCuttingToSilence()
    {
        var filter = VideoRenderService.TrimVoiceArgs("in.wav", "out.wav") is var a ? a[a.IndexOf("-af") + 1] : "";

        Assert.Contains("start_threshold=-55dB:start_silence=0.10", filter); // inicio
        Assert.Contains("start_threshold=-55dB:start_silence=0.15", filter); // final
        Assert.DoesNotContain("d=0.01", filter); // fundidos de 10 ms → click
        Assert.Equal(0, VideoRenderService.SceneGapSeconds);
    }

    // ── Tomas ancladas a la narración ──────────────────────────────────────

    [Fact]
    public void ShotReference_ParsesEachKind()
    {
        Assert.Equal(new ShotReference(ShotKind.Poster, "Cyberpunk: Edgerunners 2"), ShotReference.Parse("poster: Cyberpunk: Edgerunners 2"));
        var logo = ShotReference.Parse("logo: Studio Trigger + CD Projekt Red");
        Assert.Equal(ShotKind.Logo, logo.Kind);
        Assert.Equal(["Studio Trigger", "CD Projekt Red"], logo.Names);
        Assert.Equal(new ShotReference(ShotKind.Card, "Anime del Año", "Crunchyroll Anime Awards 2023", "poster: Cyberpunk: Edgerunners"),
            ShotReference.Parse("card: Anime del Año | Crunchyroll Anime Awards 2023 | poster: Cyberpunk: Edgerunners"));
        Assert.Equal(new ShotReference(ShotKind.Card, "10 episodios", null, null), ShotReference.Parse("card: 10 episodios"));
        Assert.Equal(ShotKind.Character, ShotReference.Parse("Character: Lucy").Kind);
        Assert.Equal(ShotKind.KeyArt, ShotReference.Parse("keyart").Kind);
        // Sin prefijo: descripción de escena, como los planes viejos.
        Assert.Equal(new ShotReference(ShotKind.Scene, "a city at night"), ShotReference.Parse("a city at night"));
    }

    [Fact]
    public void WordStarts_AlignsWhisperTimesWithTheNarration()
    {
        // Whisper real de la escena 5 de Cyberpunk: escribe "10" y "2022" en cifras.
        const string narration = "Esta miniserie de diez episodios, estrenada en septiembre de dos mil veintidós, fue una colaboración maestra entre Studio Trigger y CD Projekt Red.";
        (string, double)[] heard =
        [
            ("Esta", 0.0), ("miniserie", 0.2), ("de", 0.7), ("10", 0.8), ("episodios", 1.1), ("estrenada", 1.8),
            ("en", 2.3), ("septiembre", 2.5), ("de", 3.0), ("2022", 3.1), ("fue", 4.1), ("una", 4.4),
            ("colaboración", 4.6), ("maestra", 5.2), ("entre", 5.8), ("Studio", 6.1), ("Trigger", 6.5),
            ("y", 7.0), ("CD", 7.3), ("Projekt", 7.4), ("Red.", 7.9),
        ];
        var starts = ShotTiming.WordStarts(narration, heard, 8.4);

        Assert.Equal(5.8, ShotTiming.FindAnchor(narration, starts, "entre Studio Trigger")!.Value.Time, 3);
        Assert.Equal(7.3, ShotTiming.FindAnchor(narration, starts, "CD Projekt Red")!.Value.Time, 3);
        // "dos mil veintidós" no está en whisper: queda entre "de" (3.0) y "fue" (4.1).
        Assert.InRange(ShotTiming.FindAnchor(narration, starts, "dos mil")!.Value.Time, 3.0, 4.1);
        Assert.True(starts.Zip(starts.Skip(1)).All(p => p.First <= p.Second));
    }

    [Fact]
    public void WordStarts_UsesForcedAlignmentOfTheSpokenText_WithGlossaryRespellings()
    {
        // La voz leyó la narración con el glosario aplicado ("Agumon" → "Águmon",
        // "Greymon" → "Greimon"); el alineado devuelve las palabras de ESE texto.
        const string narration = "La digievolución de Agumon es Greymon, y eso lo cambia todo.";
        (string, double)[] heard =
        [
            ("La", 0.05), ("digievolución", 0.2), ("de", 1.1), ("Águmon", 1.3), ("es", 1.95),
            ("Greimon,", 2.15), ("y", 2.9), ("eso", 3.0), ("lo", 3.3), ("cambia", 3.45), ("todo.", 3.9),
        ];
        var starts = ShotTiming.WordStarts(narration, heard, 4.5);

        Assert.Equal(1.3, ShotTiming.FindAnchor(narration, starts, "Agumon")!.Value.Time, 3);
        Assert.Equal(2.15, ShotTiming.FindAnchor(narration, starts, "Greymon")!.Value.Time, 3);
    }

    [Fact]
    public void WordStarts_WithoutWhisper_EstimatesByLetters()
    {
        var starts = ShotTiming.WordStarts("uno dos tres cuatro", [], 8);
        Assert.Equal(0, starts[0]);
        Assert.True(starts[3] > starts[2] && starts[3] < 8);
    }

    [Fact]
    public void FindAnchor_IgnoresAccentsAndPunctuation_AndSearchesForward()
    {
        const string narration = "¿Recuerdas cuando Cyberpunk: Edgerunners llegó a Netflix? Netflix lo estrenó.";
        var starts = Enumerable.Range(0, ShotTiming.Tokens(narration).Count).Select(i => (double)i).ToArray();
        Assert.Equal(4, ShotTiming.FindAnchor(narration, starts, "llego a netflix")!.Value.Word);
        var first = ShotTiming.FindAnchor(narration, starts, "Netflix")!.Value;
        Assert.Equal(6, first.Word);
        Assert.Equal(7, ShotTiming.FindAnchor(narration, starts, "Netflix", first.Word + 1)!.Value.Word);
        Assert.Null(ShotTiming.FindAnchor(narration, starts, "Crunchyroll"));
    }

    [Fact]
    public void PlanAnchoredShots_CutsAtTheAnchors_DropsTooShort_AndFillsLongHolds()
    {
        // Escena 1 (8,4 s): tarjeta al inicio, logos en 6,1 s.
        // Escena 2 (10 s): la toma 0 duraría 0,35 s → se descarta y la 1 arranca con la escena; la 2 no tiene ancla.
        var shots = ShotTiming.PlanAnchoredShots([8.4, 10.0],
        [
            [0.0, 6.1],
            [0.0, 0.5, null],
        ]);

        Assert.Equal(0, shots[0].Slot);
        Assert.Equal(0, shots[0].Start, 3);
        Assert.Equal(1, shots[1].Slot);
        Assert.Equal(6.1 - ShotTiming.LeadSeconds, shots[1].Start, 3);
        var scene2 = shots.Where(s => s.Scene == 1).ToList();
        Assert.Equal(1, scene2[0].Slot);
        Assert.Equal(8.4, scene2[0].Start, 3);
        Assert.Equal(3.5, scene2[0].Duration, 3);
        Assert.All(scene2.Skip(1), s => Assert.Equal(-1, s.Slot));
        // Sin huecos ni solapes, y cubre todo.
        for (var i = 1; i < shots.Count; i++)
            Assert.Equal(shots[i - 1].Start + shots[i - 1].Duration, shots[i].Start, 6);
        Assert.Equal(18.4, shots[^1].Start + shots[^1].Duration, 6);
    }

    [Theory]
    [InlineData("Cyberpunk: Edgerunners 2", "Cyberpunk: Edgerunners 2", true)]
    [InlineData("Cyberpunk: Edgerunners", "Cyberpunk: Edgerunners II", false)]
    [InlineData("Frieren", "Sousou no Frieren", true)]
    [InlineData("Cyberpunk 2077", "Cyberpunk: Edgerunners", false)]
    [InlineData("Cyberpunk: Edgerunners", "Cyberpunk: Edgerunners 2", false)]
    public void SameTitle_OnlyAcceptsTheRequestedWork(string wanted, string found, bool same) =>
        Assert.Equal(same, MentionImageService.SameTitle(wanted, found));

    [Fact]
    public void InfoboxImage_ReadsTheLogoField()
    {
        const string wikitext = "{{Infobox company\n| name = Trigger\n| logo = Trigger Logo.svg\n| logo_size = 200px\n| image = [[File:Office.jpg|thumb]]\n}}";
        Assert.Equal("Trigger Logo.svg", MentionImageService.InfoboxImage(wikitext, "logo"));
        Assert.Equal("Office.jpg", MentionImageService.InfoboxImage(wikitext, "image"));
        Assert.Null(MentionImageService.InfoboxImage(wikitext, "cover"));
    }

    [Fact]
    public void VideoPlan_ParsesAnchoredShots()
    {
        var plan = VideoPlan.Parse("""
            { "title": "t", "scenes": [ { "narration": "Hola Netflix.", "shots": [ { "at": "Hola", "show": "logo: Netflix" } ] } ] }
            """);
        Assert.Equal("logo: Netflix", plan.Scenes[0].Shots![0].Show);
        Assert.Contains("\"shots\"", plan.ToJson());
    }

    // ── Detector de menciones ──────────────────────────────────────────────

    [Theory]
    [InlineData("dos mil veintidós", 2022, 3)]
    [InlineData("setenta y siete", 77, 3)]
    [InlineData("dos mil setenta y siete episodios", 2077, 5)]
    [InlineData("diez episodios", 10, 1)]
    [InlineData("veinte de octubre", 20, 1)]
    [InlineData("2023", 2023, 1)]
    public void NumberAt_ReadsSpokenNumbers(string text, long value, int length)
    {
        var n = SpanishText.NumberAt(SpanishText.Words(text), 0);
        Assert.Equal((value, length), n);
    }

    [Fact]
    public void NumberAt_ArticlesAreNotNumbers() =>
        Assert.Null(SpanishText.NumberAt(SpanishText.Words("una nueva leyenda"), 0));

    [Fact]
    public void FindCandidates_FindsNamesDatesCountsAndAwards()
    {
        var lower = new HashSet<string> { "esta", "su", "fue", "ganó" };
        var scene5 = MentionDetector.FindCandidates(
            "Esta miniserie de diez episodios, estrenada en septiembre de dos mil veintidós, fue una colaboración maestra entre Studio Trigger y CD Projekt Red.", lower);
        Assert.Equal(
            ["Count:10 episodios", "Date:Septiembre de 2022", "Name:Studio Trigger", "Name:CD Projekt Red"],
            scene5.Select(c => $"{c.Kind}:{c.Text}"));

        var scene7 = MentionDetector.FindCandidates(
            "Su éxito fue tan rotundo que ganó el Anime del Año en los Crunchyroll Anime Awards de dos mil veintitrés, y revitalizó al videojuego Cyberpunk dos mil setenta y siete.", lower);
        Assert.Contains(scene7, c => c.Text == "Anime del Año");
        Assert.Contains(scene7, c => c.Text == "Crunchyroll Anime Awards" && c.Year == 2023);
        Assert.Contains(scene7, c => c.Text == "Cyberpunk 2077");
        Assert.DoesNotContain(scene7, c => c.Text == "Su");

        var dates = MentionDetector.FindCandidates("Se estrenó el veinte de octubre de dos mil veintiséis, con Cyberpunk: Edgerunners Dos.", lower);
        Assert.Contains(dates, c => c.Kind == CandidateKind.Date && c.Text == "20 de octubre de 2026");
        Assert.Contains(dates, c => c.Kind == CandidateKind.Name && c.Text == "Cyberpunk: Edgerunners 2");
    }

    [Fact]
    public void FindCandidates_SentenceStartIsANameOnlyIfNeverLowercase()
    {
        var lower = new HashSet<string> { "vemos" };
        var found = MentionDetector.FindCandidates("Vemos a Weak. Avida era cantante. 'Watson' suena.", lower);
        Assert.Equal(["Weak", "Avida", "Watson"], found.Select(c => c.Text));
    }

    [Fact]
    public void FindCandidates_MultiWordQuotesBecomeCards()
    {
        var found = MentionDetector.FindCandidates("Muchos dijeron 'gracias, CD Projekt Red, ahora todo duele'.", new HashSet<string> { "muchos" });
        Assert.Contains(found, c => c.Kind == CandidateKind.Quote && c.Text.StartsWith("gracias"));
    }

    [Theory]
    [InlineData("Avida", "Avida Yang")]
    [InlineData("Talia", "Talia Yang")]
    [InlineData("Weak Kingsley", "Weak Kingsley")]
    [InlineData("Yang", null)] // dos personajes comparten el apellido
    [InlineData("Night City", null)]
    public void CharacterNamed_MatchesFirstOrLastName(string name, string? expected) =>
        Assert.Equal(expected, MentionDetector.CharacterNamed(name, ["Weak Kingsley", "Avida Yang", "Talia Yang"]));

    [Fact]
    public void MergeShots_KeepsGeminiShots_AddsMissingMentions_JoinsNamesSaidTogether()
    {
        var scene = new VideoScene
        {
            Narration = "Esta miniserie fue una colaboración maestra entre Studio Trigger y CD Projekt Red, y lo cambió todo para siempre en la industria del anime.",
            Shots = [new VideoShotSpec { At = "Esta miniserie", Show = "poster: Cyberpunk: Edgerunners" }],
            VisualQueries = ["CD Projekt Red logo", "a crowd cheering at a concert"],
        };
        var words = SpanishText.Words(scene.Narration);
        var shots = MentionDetector.MergeShots(scene, words,
        [
            (7, "logo: Studio Trigger", "company"),
            (10, "logo: CD Projekt Red", "company"),
        ]);

        Assert.Equal("poster: Cyberpunk: Edgerunners", shots[0].Show);
        Assert.Equal("logo: Studio Trigger + CD Projekt Red", shots[1].Show);
        Assert.Equal("Studio Trigger y", shots[1].At);
        // Hueco largo al final → relleno con la descripción visual (no con "CD Projekt Red logo").
        Assert.Equal("scene: a crowd cheering at a concert", shots[^1].Show);
    }

    [Theory]
    [InlineData("a red rose on the ground", true)]
    [InlineData("CD Projekt Red logo", false)]
    [InlineData("Weak Kingsley embracing Avida Yang", false)]
    [InlineData("a trophy for 'Anime of the Year'", false)]
    public void IsVisualDescription_OnlyKeepsWhatClipCanSee(string query, bool visual) =>
        Assert.Equal(visual, MentionDetector.IsVisualDescription(query));

    [Theory]
    [InlineData("{{Infobox company\n| name = Trigger", "company")]
    [InlineData("{{Infobox video game\n| title = Cyberpunk 2077", "work")]
    [InlineData("{{Infobox musical artist\n| name = Petite Meller", "person")]
    [InlineData("{{Infobox award\n| name = Crunchyroll Anime Awards", "award")]
    [InlineData("Sin infobox.", "")]
    public void InfoboxKind_MapsTheTemplate(string wikitext, string kind) =>
        Assert.Equal(kind, MentionImageService.InfoboxKind(wikitext));

    [Theory]
    [InlineData("CD Projekt", "CD Projekt Red", true)]
    [InlineData("Studio Trigger", "Studio Trigger", true)]
    [InlineData("Militech", "Cyberpunk 2077", false)]
    [InlineData("Kai Ikarashi", "Cyberpunk: Edgerunners 2", false)]
    [InlineData("Oslo", "Oslo (band)", true)]
    public void TitleIsName_RejectsArticlesAboutSomethingElse(string name, string title, bool same) =>
        Assert.Equal(same, MentionImageService.TitleIsName(name, title));
}
