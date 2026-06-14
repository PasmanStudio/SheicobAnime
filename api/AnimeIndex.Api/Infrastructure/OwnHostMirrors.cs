using System.Globalization;
using AnimeIndex.Api.Data.Entities;

namespace AnimeIndex.Api.Infrastructure;

/// <summary>
/// Distingue los mirrors PROPIOS (subidos por nosotros) de los embeds externos
/// de jkanime/katanime que no nos generan ingresos.
///
/// Política "de ahora en adelante": con <c>Mirrors:OwnHostsOnlySince</c> seteado
/// (fecha-hora ISO en UTC), los episodios creados EN/DESPUÉS de esa fecha muestran
/// SOLO los mirrors propios cuando tienen al menos uno. Los episodios anteriores
/// quedan INTACTOS (todos sus servidores siguen visibles) — no se toca el catálogo
/// viejo. Si la fecha no está seteada, el filtro está apagado.
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
    /// Lee <c>Mirrors:OwnHostsOnlySince</c> (ISO-8601, se asume UTC). Devuelve null
    /// si no está configurado o no parsea → el filtro queda apagado.
    /// </summary>
    public static DateTime? ParseSince(IConfiguration config)
    {
        var raw = config["Mirrors:OwnHostsOnlySince"];
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var dto)
            ? dto.UtcDateTime
            : null;
    }

    /// <summary>
    /// Para un episodio creado en/después de <paramref name="since"/>, devuelve solo
    /// los mirrors propios cuando hay ≥1 (si no, todos: nunca vacía un episodio).
    /// Episodios anteriores a <paramref name="since"/> — o si es null — se devuelven
    /// tal cual (catálogo viejo intacto).
    /// </summary>
    public static List<Mirror> Apply(List<Mirror> active, DateTime episodeCreatedAt, DateTime? since)
    {
        if (since is null) return active;
        if (episodeCreatedAt < since.Value) return active; // episodio viejo: intacto
        var own = active.Where(m => OwnProviders.Contains(m.ProviderName)).ToList();
        return own.Count > 0 ? own : active;
    }
}
