using AnimeIndex.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using WebPush;

namespace AnimeIndex.Scraper.Infrastructure.Notifications;

/// <summary>
/// Avisa a los seguidores de una serie cuando sale un episodio nuevo (doc 3).
/// Corre dentro del scraper diario (no en un cron aparte): el scraper ya detecta
/// los episodios nuevos y publica a Telegram/Discord/Instagram — esto agrega la
/// notificación in-app (campana) + Web Push.
///
/// Las tablas (series_follows, user_notifications, push_subscriptions) las creó
/// el schema de engagement por SQL crudo, no son entidades EF — se consultan con
/// Npgsql directo sobre la conexión del DbContext.
///
/// La notificación in-app se crea SIEMPRE (no necesita claves). El Web Push solo
/// se manda si VAPID está configurado.
/// </summary>
public sealed class WebPushPublisherService(
    AppDbContext db,
    WebPushSettings settings,
    ILogger<WebPushPublisherService> logger)
{
    public async Task PublishNewEpisodesAsync(CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddHours(-25);

        // Episodios nuevos × seguidores sin notificación previa (dedup por url)
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        var pending = new List<(string UserId, string Slug, string Title, string? Cover, int Ep)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT f.user_id, s.slug, s.title, s.cover_url, e.episode_number
                FROM episodes e
                JOIN series s         ON s.id = e.series_id
                JOIN series_follows f ON f.series_slug = s.slug AND f.notify
                WHERE e.is_published
                  AND e.created_at >= @since
                  AND NOT EXISTS (
                    SELECT 1 FROM user_notifications n
                    WHERE n.user_id = f.user_id
                      AND n.type = 'new_episode'
                      AND n.url = '/series/' || s.slug || '/' || e.episode_number
                  )
                ORDER BY e.created_at DESC
                LIMIT 5000
                """;
            cmd.Parameters.AddWithValue("since", since);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                pending.Add((r.GetString(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4)));
        }

        if (pending.Count == 0)
        {
            logger.LogInformation("No new-episode notifications pending");
            return;
        }

        // VAPID opcional: sin claves solo se crean notificaciones in-app
        WebPushClient? push = null;
        VapidDetails? vapid = null;
        if (settings.IsConfigured)
        {
            push = new WebPushClient();
            vapid = new VapidDetails(settings.Subject, settings.PublicKey, settings.PrivateKey);
        }
        else
        {
            logger.LogInformation("WebPush sin VAPID — solo notificaciones in-app");
        }

        var inApp = 0;
        var pushed = 0;
        var deadSubs = 0;

        foreach (var (userId, slug, title, cover, ep) in pending)
        {
            if (ct.IsCancellationRequested) break;
            var url = $"/series/{slug}/{ep}";
            var body = $"Salió el episodio {ep} — entrá a verlo";

            // 1. Notificación in-app (la campana)
            await using (var ins = conn.CreateCommand())
            {
                ins.CommandText = """
                    INSERT INTO user_notifications (user_id, type, title, body, url)
                    VALUES (@u, 'new_episode', @t, @b, @url)
                    """;
                ins.Parameters.AddWithValue("u", userId);
                ins.Parameters.AddWithValue("t", title);
                ins.Parameters.AddWithValue("b", body);
                ins.Parameters.AddWithValue("url", url);
                await ins.ExecuteNonQueryAsync(ct);
                inApp++;
            }

            // 2. Web Push a cada navegador suscripto del usuario
            if (push is null || vapid is null) continue;

            var subs = new List<(long Id, string Endpoint, string P256dh, string Auth)>();
            await using (var sq = conn.CreateCommand())
            {
                sq.CommandText = "SELECT id, endpoint, p256dh, auth FROM push_subscriptions WHERE user_id = @u";
                sq.Parameters.AddWithValue("u", userId);
                await using var sr = await sq.ExecuteReaderAsync(ct);
                while (await sr.ReadAsync(ct))
                    subs.Add((sr.GetInt64(0), sr.GetString(1), sr.GetString(2), sr.GetString(3)));
            }

            foreach (var sub in subs)
            {
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    title,
                    body,
                    url = settings.SiteUrl.TrimEnd('/') + url,
                    icon = string.IsNullOrEmpty(cover) ? settings.SiteUrl.TrimEnd('/') + "/icon-192.png" : cover,
                });
                try
                {
                    await push.SendNotificationAsync(
                        new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth), payload, vapid);
                    pushed++;
                }
                catch (WebPushException wex) when (wex.StatusCode is System.Net.HttpStatusCode.NotFound
                                                          or System.Net.HttpStatusCode.Gone)
                {
                    // Suscripción muerta (navegador desinstalado) → limpiar
                    await using var del = conn.CreateCommand();
                    del.CommandText = "DELETE FROM push_subscriptions WHERE id = @id";
                    del.Parameters.AddWithValue("id", sub.Id);
                    await del.ExecuteNonQueryAsync(ct);
                    deadSubs++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Web push falló (sub {Id}): {Msg}", sub.Id, ex.Message);
                }
            }
        }

        logger.LogInformation(
            "Notificaciones de episodio: in-app={InApp} push={Pushed} subs-muertas={Dead}",
            inApp, pushed, deadSubs);
    }
}

/// <summary>VAPID + URL del sitio para los avisos push. Bind de "WebPush".</summary>
public sealed class WebPushSettings
{
    public string PublicKey { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    /// <summary>mailto: o URL del sitio — requerido por el estándar VAPID.</summary>
    public string Subject { get; set; } = "https://sheicobanime.sheicob.workers.dev";
    public string SiteUrl { get; set; } = "https://sheicobanime.sheicob.workers.dev";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(PublicKey)
                             && !string.IsNullOrWhiteSpace(PrivateKey);
}
