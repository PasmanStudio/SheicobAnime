using AnimeIndex.Api.Data.Entities;

namespace AnimeIndex.Api.Infrastructure;

/// <summary>
/// Distingue los mirrors PROPIOS (subidos por nosotros) de los embeds externos
/// de jkanime/katanime que no nos generan ingresos.
///
/// Con el flag <c>Mirrors:OwnHostsOnly</c> activo, los endpoints que sirven
/// mirrors muestran SOLO los propios cuando el episodio tiene al menos uno —
/// si no tiene ninguno propio todavía, caen a todos (fallback seguro: nunca se
/// deja un episodio sin servidores).
/// </summary>
public static class OwnHostMirrors
{
    /// <summary>
    /// Provider names propios. "voe-sa" es nuestra subida a Voe — distinta del
    /// "voe" a secas, que es un embed de jkanime que NO nos paga.
    /// </summary>
    public static readonly HashSet<string> OwnProviders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "seekstreaming", // player propio (limpio, sin ads)
            "doodstream",    // reparto de ingresos
            "voe-sa",        // reparto de ingresos (Voe propio)
        };

    /// <summary>
    /// Si <paramref name="ownOnly"/> está activo y hay ≥1 mirror propio, devuelve
    /// solo los propios; si no hay ninguno propio, devuelve <paramref name="active"/>
    /// tal cual (nunca vacía un episodio).
    /// </summary>
    public static List<Mirror> Apply(List<Mirror> active, bool ownOnly)
    {
        if (!ownOnly) return active;
        var own = active.Where(m => OwnProviders.Contains(m.ProviderName)).ToList();
        return own.Count > 0 ? own : active;
    }
}
