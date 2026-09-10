using AnimeIndex.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AnimeIndex.Scraper.Infrastructure.Instagram;

/// <summary>
/// Trae de vuelta las métricas de los reels ya publicados y las guarda en
/// <c>anime_news_items</c>, al lado del titular que las produjo.
///
/// Existe porque hasta sep-2026 el loop entre publicar y medir estaba abierto:
/// el export a CSV baja las ~800 piezas de la cuenta pero no sabe QUÉ noticia
/// era cada una, así que responder "¿qué tipo de titular funciona?" pedía cruzar
/// a mano el CSV contra los logs. Y como cada medición costaba una corrida
/// manual de 8 minutos, se medía cada varios meses y en el medio se publicaban
/// 7 piezas por día a ciegas.
///
/// Con esto, cada pregunta futura es una query — y el selector de la noticia del
/// día puede hacer few-shot con nuestros propios resultados en vez de con la
/// intuición genérica del modelo (ver BuildPastPerformanceExamplesAsync).
///
/// Se corre semanalmente. No es idempotente por elección: las métricas de un
/// reel siguen subiendo durante días, así que re-sincronizar una pieza vieja es
/// deseable, no un problema.
/// </summary>
public class NewsInsightsSyncService(
    AppDbContext db,
    InstagramInsightsService insights,
    ILogger<NewsInsightsSyncService> logger)
{
    /// <summary>
    /// Sincroniza los reels publicados en los últimos <paramref name="days"/>
    /// días. Devuelve cuántas filas se actualizaron.
    /// </summary>
    public async Task<int> SyncAsync(int days = 60, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-days);

        var reels = await db.AnimeNewsItems
            .Where(n => n.IgReelMediaId != null && n.IgPostedAt >= since)
            .OrderByDescending(n => n.IgPostedAt)
            .ToListAsync(ct);

        if (reels.Count == 0)
        {
            logger.LogInformation("Insights sync: no hay reels publicados en los últimos {Days} días", days);
            return 0;
        }

        logger.LogInformation("Insights sync: {Count} reels a sincronizar", reels.Count);

        var updated = 0;
        var failed = 0;
        foreach (var item in reels)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var m = await insights.FetchMetricsAsync(item.IgReelMediaId!, "REELS", ct);
                if (m.Count == 0)
                {
                    // Una pieza sin métricas es normal (recién publicada, o Meta
                    // todavía no las calculó): no se pisa lo que ya había.
                    continue;
                }

                item.IgReelViews             = Get(m, "views");
                item.IgReelReach             = Get(m, "reach");
                item.IgReelShares            = Get(m, "shares");
                item.IgReelSaved             = Get(m, "saved");
                item.IgReelComments          = Get(m, "comments");
                item.IgReelTotalInteractions = Get(m, "total_interactions");

                // Meta devuelve ig_reels_avg_watch_time en MILISEGUNDOS. Guardarlo
                // crudo dejaba números como "6900" donde el playbook habla de
                // 6,9 s, así que se normaliza acá y no en cada consulta.
                var watchMs = Get(m, "ig_reels_avg_watch_time");
                item.IgReelAvgWatchSeconds = watchMs is null ? null : Math.Round(watchMs.Value / 1000.0, 2);

                // reels_skip_rate llega como porcentaje entero. Meta lo reporta de
                // forma despareja (37 de 302 piezas en el export de sep-2026), así
                // que queda null muy seguido — y eso es un dato, no un error.
                item.IgReelSkipRate = Get(m, "reels_skip_rate");

                item.IgInsightsAt = DateTime.UtcNow;
                updated++;
            }
            catch (InvalidOperationException)
            {
                // ESTA excepción no es "falló una pieza": FetchMetricsAsync la
                // lanza solo cuando NINGUNA métrica funciona ni pedida de a una,
                // que es su forma deliberada de decir "es el token, no la
                // métrica". Tragársela dejaba la corrida en verde con
                // "0 actualizados, 59 sin métricas todavía", que se lee igual que
                // "todavía no hay datos". El workflow corre --token-scopes antes,
                // pero una invocación manual del CLI no tiene esa red.
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Un media borrado a mano desde la app devuelve 400 y no debería
                // tumbar la sincronización de los otros 59.
                failed++;
                logger.LogWarning(ex, "Insights sync: falló el reel {MediaId} (\"{Title}\")",
                    item.IgReelMediaId, Truncate(item.Title, 50));
            }
        }

        await db.SaveChangesAsync(CancellationToken.None);

        logger.LogInformation(
            "Insights sync: {Updated} actualizados, {Failed} con error, {Skipped} sin métricas todavía",
            updated, failed, reels.Count - updated - failed);
        return updated;
    }

    /// <summary>
    /// Resumen de lo medido, para que la corrida deje algo legible en los logs
    /// en vez de solo un contador. La mediana y no el promedio a propósito: la
    /// distribución es de cola larga (el top 10 se lleva el 39 % de las views),
    /// así que el promedio no describe a ninguna pieza real.
    /// </summary>
    public async Task<string> SummaryAsync(int days = 60, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var rows = await db.AnimeNewsItems
            .Where(n => n.IgReelViews != null && n.IgPostedAt >= since)
            .Select(n => new { Views = n.IgReelViews!.Value, n.IgReelAvgWatchSeconds, n.IgReelDurationSeconds })
            .ToListAsync(ct);

        if (rows.Count == 0) return "sin reels medidos todavía";

        var views = rows.Select(r => (double)r.Views).OrderBy(v => v).ToList();
        var total = views.Sum();

        // Retención real = watch time ÷ duración. Solo se puede calcular en las
        // piezas publicadas después de que se empezó a guardar la duración.
        var withDuration = rows
            .Where(r => r.IgReelAvgWatchSeconds > 0 && r.IgReelDurationSeconds > 0)
            .Select(r => r.IgReelAvgWatchSeconds!.Value / r.IgReelDurationSeconds!.Value)
            .OrderBy(x => x)
            .ToList();

        var retention = withDuration.Count == 0
            ? "sin datos"
            : $"{Median(withDuration) * 100:F0} % (n={withDuration.Count})";

        return $"{rows.Count} reels · views mediana {Median(views):F0} · total {total:F0} · "
             + $"retención mediana {retention}";
    }

    private static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;

    private static long? Get(IReadOnlyDictionary<string, long> m, string key) =>
        m.TryGetValue(key, out var v) ? v : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
