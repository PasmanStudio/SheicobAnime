using AnimeIndex.Scraper.Infrastructure.YouTube;

namespace AnimeIndex.Api.Tests;

/// <summary>Dirección del video largo: de qué fuente sale cada escena y qué ya se presentó.</summary>
public class YouTubeDirectorTests
{
    private static readonly List<FootageSource> Sources =
    [
        new() { Id = "s1-trailer", Kind = "trailer", Season = 1, Keywords = ["primer Cyberpunk: Edgerunners"] },
        new() { Id = "s2-trailer", Kind = "trailer", Season = 2, Keywords = ["Edgerunners Dos"] },
        new() { Id = "s2-teaser", Kind = "teaser", Season = 2, Keywords = ["Edgerunners Dos"] },
        new() { Id = "mv-watson", Kind = "mv", Season = 2, Keywords = ["Watson", "Petite Meller", "Oslo Green"] },
    ];

    private static VideoPlan Plan(params string[] narrations) => new()
    {
        Scenes = narrations.Select(n => new VideoScene { Narration = n }).ToList(),
    };

    [Fact]
    public void Router_SendsEachSceneToTheSourceItTalksAbout()
    {
        var plan = Plan(
            "¿Recuerdas cuando llegó a Netflix?",                                        // sin pistas → primera fuente
            "Primero debemos recordar el impacto del primer Cyberpunk: Edgerunners.",    // T1
            "Fue una colaboración entre Studio Trigger y CD Projekt Red.",                // sigue T1
            "Ahora, con Cyberpunk: Edgerunners Dos, tenemos una nueva leyenda.",         // T2: tráiler y teaser
            "'Watson', el tema de cierre de Cyberpunk: Edgerunners Dos, por Petite Meller.", // el videoclip gana
            "Es una animación original que nos revela cómo se conocieron.",              // sigue el videoclip
            "Cuando lo conocemos en la segunda temporada, su vida es muy diferente.");   // temporada 2

        var routes = FootageRouter.Route(plan, Sources);

        Assert.Equal(["s1-trailer"], routes[0].Ids);
        Assert.Equal(["s1-trailer"], routes[1].Ids);
        Assert.Equal(["s1-trailer"], routes[2].Ids);
        Assert.Contains("escena 2", routes[2].Reason);
        Assert.Equal(["s2-trailer", "s2-teaser"], routes[3].Ids);
        Assert.Equal(["mv-watson"], routes[4].Ids);
        Assert.Equal(["mv-watson"], routes[5].Ids);
        // "segunda temporada" suma 1 a las tres fuentes de la T2: empatan.
        Assert.Equal(["s2-trailer", "s2-teaser", "mv-watson"], routes[6].Ids);
    }

    [Fact]
    public void Router_MusicVideoCueBeatsTheSeasonName()
    {
        var routes = FootageRouter.Route(Plan("Este videoclip enriquece la historia de Cyberpunk: Edgerunners Dos."), Sources);
        Assert.Equal(["mv-watson"], routes[0].Ids);
    }

    [Fact]
    public void Router_SceneFootageInThePlanWins()
    {
        var plan = Plan("Los creadores dijeron que no habría una segunda temporada.");
        plan.Scenes[0].Footage = ["s1-trailer", "no-existe"];

        var routes = FootageRouter.Route(plan, Sources);

        Assert.Equal(["s1-trailer"], routes[0].Ids);
        Assert.Equal("elegido en el plan", routes[0].Reason);
    }

    [Theory]
    [InlineData("la segunda temporada", 2)]
    [InlineData("en la temporada 3 vuelve", 3)]
    [InlineData("la temporada dos", 2)]
    [InlineData("septiembre de dos mil veintidós", null)]
    public void MentionedSeason_ReadsOrdinalsAndNumbers(string text, int? expected) =>
        Assert.Equal(expected, FootageRouter.MentionedSeason(FootageRouter.Normalize(text)));

    [Theory]
    [InlineData("s2-trailer", "https://www.youtube.com/watch?v=abc", true)]
    [InlineData("../fuera", "https://www.youtube.com/watch?v=abc", false)]
    [InlineData("C:\\x", "https://www.youtube.com/watch?v=abc", false)]
    [InlineData("mv", "--exec=calc", false)]
    [InlineData("mv", "file:///etc/passwd", false)]
    public void Manifest_RejectsIdsThatEscapeFootageAndUrlsThatAreNotHttps(string id, string url, bool ok) =>
        Assert.Equal(ok, FootageSource.IsValidId(id) && FootageSource.IsValidUrl(url));

    private static VideoPlan Shots(params (string At, string Show)[][] scenes) => new()
    {
        Scenes = scenes.Select(s => new VideoScene
        {
            Narration = string.Join(" ", s.Select(x => x.At)),
            Shots = s.Select(x => new VideoShotSpec { At = x.At, Show = x.Show }).ToList(),
        }).ToList(),
    };

    [Fact]
    public void Ledger_ShowsEachPosterAndCharacterOnlyOnce()
    {
        var plan = Shots(
            [("Cyberpunk Edgerunners", "poster: Cyberpunk: Edgerunners"), ("Weak Kingsley y", "character: Weak Kingsley + Avida Yang")],
            [("Cyberpunk Edgerunners", "poster: Cyberpunk: Edgerunners"), ("una escena", "scene: a city at night")],
            [("Weak Kingsley y", "character: Weak Kingsley + Avida Yang"), ("Talia", "character: Talia Yang + Avida Yang")]);

        var result = IntroductionLedger.Apply(plan);

        Assert.NotNull(result.Specs[0][0]);
        Assert.NotNull(result.Specs[0][1]);
        Assert.Null(result.Specs[1][0]);                       // póster repetido → metraje
        Assert.Equal(ShotKind.Scene, result.Specs[1][1]!.Kind);  // las escenas no se registran
        Assert.Null(result.Specs[2][0]);                       // los dos ya presentados
        Assert.Equal("Talia Yang", result.Specs[2][1]!.Value);  // solo la que falta
        Assert.Equal(3, result.Changes.Count);
        Assert.Contains("escena 1", result.Changes[0].Reason);
    }

    [Fact]
    public void Ledger_LogosOnlyAddTheNewCompanies()
    {
        var plan = Shots(
            [("Netflix", "logo: Netflix")],
            [("Netflix y CD", "logo: Netflix + CD Projekt Red")],
            [("CD Projekt Red", "logo: CD Projekt Red")]);

        var result = IntroductionLedger.Apply(plan);

        Assert.Equal("CD Projekt Red", result.Specs[1][0]!.Value);
        Assert.Null(result.Specs[2][0]);
    }

    [Fact]
    public void Ledger_CardsDoNotRepeatAndDropAnImageAlreadyShown()
    {
        var plan = Shots(
            [("Night City", "card: Night City"), ("Edgerunners", "poster: Cyberpunk: Edgerunners")],
            [("diez episodios", "card: 10 episodios | poster: Cyberpunk: Edgerunners")],
            [("diez episodios", "card: 10 episodios | poster: Cyberpunk: Edgerunners 2"), ("Night City", "card: Night City")]);

        var result = IntroductionLedger.Apply(plan);

        var t1 = result.Specs[1][0]!;
        Assert.Equal("10 episodios", t1.Value);
        Assert.Null(t1.Image);                                   // el póster ya se vio: la tarjeta va sola
        Assert.Equal("poster: Cyberpunk: Edgerunners 2", result.Specs[2][0]!.Image); // otro dato, otra tarjeta
        Assert.Null(result.Specs[2][1]);                         // "Night City" otra vez → metraje
    }

    [Fact]
    public void Ledger_ACardThatLosesItsImageCannotLookLikeOneAlreadyShown()
    {
        var plan = Shots(
            [("Edgerunners", "poster: Cyberpunk: Edgerunners"), ("Dos", "poster: Cyberpunk: Edgerunners 2")],
            [("diez episodios", "card: 10 episodios | poster: Cyberpunk: Edgerunners")],
            [("diez episodios", "card: 10 episodios | poster: Cyberpunk: Edgerunners 2")]);

        var result = IntroductionLedger.Apply(plan);

        Assert.NotNull(result.Specs[1][0]);  // "10 episodios" sola, la primera vez
        Assert.Null(result.Specs[2][0]);     // sin su póster quedaría idéntica → metraje
    }

    private static FootagePool Pool(string id, params (double Time, int Moment)[] segments) =>
        new(id, "trailer", id + ".mp4", segments.Select(s => new FootageSegment(s.Time - 1, 2, $"{id}_{s.Time}.jpg", s.Time, s.Moment)).ToList());

    [Fact]
    public void Library_ClipMatchStaysInTheSceneSourceAndSkipsUsedMoments()
    {
        // El plano 1 del teaser es el mismo que el 0 del tráiler (momento 0).
        var library = new FootageLibrary([Pool("trailer", (10, 0), (30, 1)), Pool("teaser", (5, 0), (20, 2))]);
        // Índices en Stills: 0 trailer@10, 1 trailer@30, 2 teaser@5, 3 teaser@20.
        var ranked = new List<(int, float)> { (0, 0.31f), (2, 0.30f), (3, 0.29f), (1, 0.28f) };

        var first = library.PickMatched(ranked, ["teaser"], 0.2f, 2);
        var second = library.PickMatched(ranked, ["teaser"], 0.2f, 2);
        var third = library.PickMatched(ranked, ["trailer"], 0.2f, 2);

        Assert.Equal(("teaser", 5.0), (first!.Pool.Id, first.Segment.Time));
        Assert.Equal(("teaser", 20.0), (second!.Pool.Id, second.Segment.Time));
        Assert.Equal(("trailer", 30.0), (third!.Pool.Id, third.Segment.Time)); // trailer@10 = teaser@5, ya usado
        Assert.Null(library.PickMatched(ranked, ["trailer"], 0.2f, 2));
        Assert.Null(library.PickMatched([(1, 0.1f)], [], 0.2f, 2));             // bajo el umbral
    }

    [Fact]
    public void Library_FillerUsesTheSceneSourceFirstAndNeverRepeatsWhileOthersRemain()
    {
        var library = new FootageLibrary([Pool("mv", (10, 0), (40, 1)), Pool("trailer", (10, 2))]);

        var picks = Enumerable.Range(0, 3).Select(_ => library.PickFiller(["mv"], preferClip: true, duration: 2)!).ToList();

        Assert.Equal(["mv", "mv", "trailer"], picks.Select(p => p.Pool.Id));   // el videoclip se agota → otra fuente
        Assert.Equal(3, picks.Select(p => p.Segment.Moment).Distinct().Count());
        Assert.True(picks[0].AsClip);
        Assert.Equal("mv", library.PickFiller(["mv"], preferClip: false, duration: 2)!.Pool.Id); // todo usado → repite de la escena
        Assert.Null(new FootageLibrary([]).PickFiller(["mv"], true, 2));
    }
}
