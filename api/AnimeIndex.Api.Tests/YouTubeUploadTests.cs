using System.Text;
using System.Text.Json;
using AnimeIndex.Scraper.Infrastructure.Instagram;
using AnimeIndex.Scraper.Infrastructure.YouTube;

namespace AnimeIndex.Api.Tests;

public class YouTubeUploadTests
{
    private static readonly YouTubeSettings Settings = new() { SiteUrl = "https://example.test" };

    private static VideoPlan Plan(string title = "La teoría del poder oculto de Frieren") => new()
    {
        Title = title,
        Description = "Por qué Frieren esconde su maná desde hace mil años.",
        Tags = ["Frieren", "anime", "teorías de anime"],
        Sources = ["https://anilist.co/anime/154587", "https://es.wikipedia.org/wiki/Frieren"],
        Mood = "dark",
    };

    private static ReelTrack Cc(string title) =>
        new(title, "https://incompetech.com/x.mp3", "dark", 0, $"Música: {title} — Kevin MacLeod (incompetech.com) · CC BY 4.0");

    [Fact]
    public void Build_SetsCategoryLanguagesAndStatus()
    {
        var r = YouTubeMetadata.Build(Plan(), Settings, []);

        Assert.Equal("24", r.Snippet.CategoryId);
        Assert.Equal("es-419", r.Snippet.DefaultLanguage);
        Assert.Equal("es-419", r.Snippet.DefaultAudioLanguage);
        Assert.Equal("private", r.Status.PrivacyStatus);
        Assert.False(r.Status.SelfDeclaredMadeForKids);
        Assert.True(r.Status.ContainsSyntheticMedia);
    }

    [Fact]
    public void Build_SerializesWithApiFieldNames()
    {
        var json = JsonSerializer.Serialize(YouTubeMetadata.Build(Plan(), Settings, []));

        Assert.Contains("\"snippet\":{\"title\":", json);
        Assert.Contains("\"categoryId\":\"24\"", json);
        Assert.Contains("\"selfDeclaredMadeForKids\":false", json);
        Assert.Contains("\"containsSyntheticMedia\":true", json);
        Assert.Contains("\"privacyStatus\":\"private\"", json);
    }

    [Theory]
    [InlineData("public", "public")]
    [InlineData(" Unlisted ", "unlisted")]
    [InlineData("", "private")]
    [InlineData("cualquiera", "private")]
    [InlineData(null, "private")]
    public void NormalizePrivacy_DefaultsToPrivate(string? input, string expected) =>
        Assert.Equal(expected, YouTubeMetadata.NormalizePrivacy(input));

    [Fact]
    public void Description_FollowsChannelTemplate()
    {
        var d = YouTubeMetadata.Description(Plan(), Settings, [Cc("Killers"), Cc("Achilles")]);

        Assert.Equal(
            "Por qué Frieren esconde su maná desde hace mil años.\n\n" +
            "📺 Episodios y calendario de la temporada: https://example.test\n" +
            "\nFuentes:\n- https://anilist.co/anime/154587\n- https://es.wikipedia.org/wiki/Frieren\n" +
            "\nMúsica:\n\"Killers\" Kevin MacLeod (incompetech.com)\n\"Achilles\" Kevin MacLeod (incompetech.com)\n" +
            "Licensed under Creative Commons: By Attribution 4.0 License\n" +
            "http://creativecommons.org/licenses/by/4.0/\n" +
            "\nNarración con voz sintética e imágenes generadas con IA.\n\n" +
            "#anime #noticiasanime #teoriasanime",
            d);
    }

    [Fact]
    public void Description_AddsInstagramOnlyWhenConfigured_AndSkipsUncreditedTracks()
    {
        var settings = new YouTubeSettings { SiteUrl = "https://example.test", InstagramHandle = "@sheicob" };
        var own = new ReelTrack("Propio", "https://x/y.mp3", "dark");

        var d = YouTubeMetadata.Description(Plan(), settings, [own]);

        Assert.Contains("📸 Instagram: @sheicob\n", d);
        Assert.DoesNotContain("Música:", d);
        Assert.DoesNotContain("📸", YouTubeMetadata.Description(Plan(), Settings, []));
    }

    [Fact]
    public void Description_StripsAngleBracketsAndFitsTheByteLimit()
    {
        var plan = Plan();
        plan.Description = "<b>" + string.Concat(Enumerable.Repeat("Maná y más maná 🔥 ", 400)) + "</b>";

        var d = YouTubeMetadata.Description(plan, Settings, [Cc("Killers")]);

        Assert.DoesNotContain('<', d);
        Assert.DoesNotContain('>', d);
        Assert.True(Encoding.UTF8.GetByteCount(d) <= YouTubeMetadata.MaxDescriptionBytes);
        // Lo que se recorta es el resumen: los créditos CC BY y el aviso de IA quedan.
        Assert.Contains("\"Killers\" Kevin MacLeod (incompetech.com)", d);
        Assert.Contains("Narración con voz sintética", d);
        Assert.Contains("…", d);
    }

    [Fact]
    public void Title_TrimsToHundredCharsOnAWordBoundary()
    {
        var t = YouTubeMetadata.Title("Frieren <y> " + string.Concat(Enumerable.Repeat("palabra ", 20)));

        Assert.True(t.Length <= 100);
        Assert.DoesNotContain('<', t);
        Assert.EndsWith("palabra", t);
    }

    [Fact]
    public void Title_Empty_Throws() =>
        Assert.Throws<InvalidOperationException>(() => YouTubeMetadata.Title("  <> "));

    [Fact]
    public void Tags_DedupesCleansAndAddsChannelTag()
    {
        var tags = YouTubeMetadata.Tags(["Frieren", "frieren", "#anime", "a, b", "  ", "<x>"]);

        Assert.Equal(["Frieren", "anime", "a b", "x", "SheicobAnime"], tags);
    }

    [Fact]
    public void Tags_StayUnderFiveHundredChars()
    {
        var many = Enumerable.Range(0, 100).Select(i => $"tag largo número {i}");

        var tags = YouTubeMetadata.Tags(many);

        // Los tags con espacios cuentan las comillas que YouTube les agrega.
        var used = tags.Sum(t => t.Length + (t.Contains(' ') ? 2 : 0)) + tags.Count - 1;
        Assert.True(used <= YouTubeMetadata.MaxTagsLength);
        Assert.Contains("SheicobAnime", tags);
    }

    [Theory]
    [InlineData("La teoría del poder oculto de Frieren", "Teorías de anime")]
    [InlineData("Noticia: anuncian la temporada 2 de Frieren", "Noticias de anime")]
    [InlineData("Qué ver en el anime de temporada de invierno", "Anime de temporada")]
    [InlineData("El final de Frieren explicado", "Análisis y explicaciones")]
    [InlineData("Frieren", "Teorías de anime")]
    public void PickPlaylist_InfersFromTitle(string title, string expected)
    {
        var plan = Plan(title);
        plan.Tags = [];
        Assert.Equal(expected, YouTubeMetadata.PickPlaylist(plan));
    }

    [Fact]
    public void PickPlaylist_RequestedWins() =>
        Assert.Equal("Noticias de anime", YouTubeMetadata.PickPlaylist(Plan(), " Noticias de anime "));

    [Theory]
    [InlineData("Anime de temporada (recomendaciones)", "Anime de temporada", true)]
    [InlineData("Teorias de Anime", "Teorías de anime", true)]
    [InlineData("Noticias de anime", "Teorías de anime", false)]
    public void PlaylistMatches_IgnoresAccentsCaseAndSuffix(string title, string wanted, bool expected) =>
        Assert.Equal(expected, YouTubeMetadata.PlaylistMatches(title, wanted));
}
