using AnimeIndex.Scraper.Infrastructure.Instagram;

namespace AnimeIndex.Api.Tests;

/// <summary>
/// Regresión del bug que dejó semanas de reels sin video (dx 9-sep-2026).
///
/// La búsqueda de tráiler tiene dos escalones: primero la versión latina, y si
/// no existe, el mismo tráiler oficial en cualquier idioma. El segundo escalón
/// se armaba con `query.Replace("espanol latino", "", OrdinalIgnoreCase)` —
/// SIN la ñ — mientras que tanto el prompt de la IA como HeuristicVideoQuery
/// generan la query CON tilde ("español latino"). OrdinalIgnoreCase no pliega
/// acentos, así que el Replace nunca sacaba nada y el segundo escalón buscaba
/// en YouTube exactamente la misma frase. Para un estreno sin doblaje latino
/// eso no devuelve nada y el reel salía como slideshow.
/// </summary>
public class TrailerQueryLadderTests
{
    [Theory]
    // La forma que realmente genera el sistema (con tilde y con ñ).
    [InlineData("Kaiju No. 8 temporada 2 tráiler oficial español latino",
                "Kaiju No. 8 temporada 2 tráiler oficial")]
    // La forma sin acentos también, por si alguna query llega normalizada.
    [InlineData("Kaiju No. 8 tráiler oficial espanol latino",
                "Kaiju No. 8 tráiler oficial")]
    // "español" solo, sin "latino".
    [InlineData("One Piece tráiler oficial español", "One Piece tráiler oficial")]
    // Variante de España.
    [InlineData("One Piece trailer castellano", "One Piece trailer")]
    public void StripLanguageQualifier_RemovesTheLanguageHint(string input, string expected)
    {
        Assert.Equal(expected, AnimeNewsPublisherService.StripLanguageQualifier(input));
    }

    [Fact]
    public void StripLanguageQualifier_ActuallyChangesTheAccentedForm()
    {
        // El corazón del bug: la query real DEBE quedar distinta después del
        // recorte. Si vuelve igual, el segundo escalón de la escalera es un
        // duplicado del primero y no busca en "cualquier idioma".
        const string real = "Kaiju No. 8 temporada 2 tráiler oficial español latino";

        var stripped = AnimeNewsPublisherService.StripLanguageQualifier(real);

        Assert.NotEqual(real, stripped);
        Assert.DoesNotContain("español", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("latino", stripped, StringComparison.OrdinalIgnoreCase);
        // Pero el contenido que importa sobrevive.
        Assert.Contains("Kaiju No. 8", stripped);
        Assert.Contains("tráiler oficial", stripped);
    }

    [Fact]
    public void StripLanguageQualifier_DoesNotMaulUnrelatedWords()
    {
        // \b evita que "españoles" pierda la mitad de la palabra.
        const string q = "Los españoles trailer oficial";
        Assert.Equal(q, AnimeNewsPublisherService.StripLanguageQualifier(q));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void StripLanguageQualifier_HandlesEmpty(string input)
    {
        Assert.Equal(input, AnimeNewsPublisherService.StripLanguageQualifier(input));
    }
}
