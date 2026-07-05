using AnimeIndex.Scraper.Infrastructure.Importers;

namespace AnimeIndex.Api.Tests;

public class SeriesImportPickBestTests
{
    // Los 4 candidatos reales que devolvió animeav1 para "Mushoku Tensei III"
    // (del log del run que importó la temporada equivocada).
    private static readonly SourceSeriesRef[] MushokuCandidates =
    [
        new("mushoku-tensei-iii-isekai-ittara-honki-dasu", "Mushoku Tensei III"),
        new("mushoku-tensei-ii-isekai-ittara-honki-dasu", "Mushoku Tensei II"),
        new("mushoku-tensei-isekai-ittara-honki-dasu", "Mushoku Tensei"),
        new("mushoku-no-eiyuu-betsu-ni-skill-nanka-iranakatta-n-da-ga", "Mushoku no Eiyuu"),
    ];

    [Fact]
    public void PickBest_WithRomanNumeralAndColon_PicksCorrectSeason()
    {
        // El ":" en "III:" hacía que el token "iii:" nunca matcheara → empataba
        // con la temporada 1 y el desempate por slug corto elegía la 1. Ahora
        // la tokenización limpia la puntuación y gana la temporada 3.
        var pick = SeriesImportService.PickBest(
            "Mushoku Tensei III: Isekai Ittara Honki Dasu", MushokuCandidates);

        Assert.Equal("mushoku-tensei-iii-isekai-ittara-honki-dasu", pick);
    }

    [Fact]
    public void PickBest_SecondSeasonQuery_PicksSecondSeason()
    {
        var pick = SeriesImportService.PickBest(
            "Mushoku Tensei II: Isekai Ittara Honki Dasu", MushokuCandidates);

        Assert.Equal("mushoku-tensei-ii-isekai-ittara-honki-dasu", pick);
    }

    [Fact]
    public void PickBest_BaseTitle_PrefersShortestBaseSlug()
    {
        // "frieren" → la serie base, no la 2da temporada
        var results = new[]
        {
            new SourceSeriesRef("sousou-no-frieren-2nd-season", "Frieren 2"),
            new SourceSeriesRef("sousou-no-frieren", "Frieren"),
        };

        Assert.Equal("sousou-no-frieren", SeriesImportService.PickBest("frieren", results));
    }
}
