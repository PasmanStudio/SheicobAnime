using AnimeIndex.Scraper.Infrastructure.Importers;
using AnimeIndex.Scraper.Jobs;
using Microsoft.Extensions.Configuration;

namespace AnimeIndex.Api.Tests;

/// <summary>Cron diario sobre animeav1 (Source3Strategy): parser de la home, filtro de embeds y fuentes.</summary>
public class AnimeAv1SourceTests
{
    [Fact]
    public void ParseLatestEpisodes_ExtractsSlugAndNumber_InOrder_Deduped()
    {
        // Estructura real de la home: cada card linkea a /media/{slug}/{n}; además hay
        // links a la serie sola (/media/{slug}) que no son episodios.
        const string html = """
            <a href="/media/dogulwang/12" class="card">Dogulwang 12</a>
            <a href="/media/dogulwang">Dogulwang</a>
            <a href="/media/rezero-kara-hajimeru-isekai-seikatsu-4th-season/19">Re:Zero 19</a>
            <a href="/media/dogulwang/12">duplicado</a>
            <a href="/media/keroro-gunsou/0">episodio 0 inválido</a>
            """;

        var latest = AnimeAv1Importer.ParseLatestEpisodes(html);

        Assert.Equal(
            [("dogulwang", (short)12), ("rezero-kara-hajimeru-isekai-seikatsu-4th-season", (short)19)],
            latest);
    }

    [Fact]
    public void ClassifyEmbed_RealEpisodeList_KeepsOnlyPlayableEmbeds()
    {
        // Lista SUB real de /media/dogulwang/12 (oct-2026).
        SourceEmbed[] embeds =
        [
            new("UPNShare", "https://animeav1.uns.bio/#hrp6yr"),
            new("Voe", "https://voe.sx/e/5a5gcbil7ho8"),
            new("Byse", "https://byselapuix.com/e/02338uiuk9da"),
            new("MP4Upload", "https://www.mp4upload.com/embed-2my68j85x30k.html"),
            new("TransferIt", "https://transfer.it/t/CuCinSxFUb7D"),
            new("Mega", "https://mega.nz/file/JERwmILB#key"),
            new("1Fichier", "https://1fichier.com/?gyp35nafrnwauuo7h2uc"),
            new("MP4Upload", "https://www.mp4upload.com/2my68j85x30k"),
        ];

        var kept = embeds
            .Select(e => (e.Url, C: SeriesImportService.ClassifyEmbed(e)))
            .Where(x => x.C is not null)
            .Select(x => (x.Url, x.C!.Value.Provider))
            .ToList();

        Assert.Equal(
            [
                ("https://voe.sx/e/5a5gcbil7ho8", "voe"),
                ("https://byselapuix.com/e/02338uiuk9da", "byse"),
                ("https://www.mp4upload.com/embed-2my68j85x30k.html", "mp4upload"),
            ],
            kept);
    }

    [Fact]
    public void GetAutoSources_DefaultsToJkanime()
    {
        var config = new ConfigurationBuilder().Build();

        Assert.Equal(["source2"], ScrapeSchedulerJob.GetAutoSources(config));
    }

    [Fact]
    public void GetAutoSources_ReadsCommaSeparatedConfig()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scraper:AutoSources"] = " source3 , " })
            .Build();

        Assert.Equal(["source3"], ScrapeSchedulerJob.GetAutoSources(config));
    }
}
