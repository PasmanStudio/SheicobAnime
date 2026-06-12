// Push de episodios nuevos para series seguidas (doc 3, fase 3).
//
// Para cada episodio publicado en la última ventana, busca los usuarios que
// siguen la serie (series_follows.notify), crea la notificación in-app
// (user_notifications) y manda Web Push a todas sus suscripciones.
// Dedup: NOT EXISTS sobre user_notifications por (user, url) — correr el
// script de más nunca duplica avisos.
//
// Env requerido:
//   DATABASE_URL       — postgres:// URI (mismo secret que usa el scraper)
//   VAPID_PUBLIC_KEY   — par VAPID (generar con: npx web-push generate-vapid-keys)
//   VAPID_PRIVATE_KEY
//   SITE_URL           — opcional, default https://sheicobanime.sheicob.workers.dev
//   LOOKBACK_HOURS     — opcional, default 26 (cubre el cron diario con margen)

import pg from "pg";
import webpush from "web-push";

const DATABASE_URL = process.env.DATABASE_URL;
const VAPID_PUBLIC_KEY = process.env.VAPID_PUBLIC_KEY;
const VAPID_PRIVATE_KEY = process.env.VAPID_PRIVATE_KEY;
const SITE_URL = (process.env.SITE_URL ?? "https://sheicobanime.sheicob.workers.dev").replace(/\/$/, "");
const LOOKBACK_HOURS = Number(process.env.LOOKBACK_HOURS ?? 26);

if (!DATABASE_URL) {
  console.error("Falta DATABASE_URL");
  process.exit(1);
}

const pushEnabled = Boolean(VAPID_PUBLIC_KEY && VAPID_PRIVATE_KEY);
if (pushEnabled) {
  webpush.setVapidDetails(`${SITE_URL}/dmca`, VAPID_PUBLIC_KEY, VAPID_PRIVATE_KEY);
} else {
  console.warn("Sin claves VAPID — se crean notificaciones in-app pero no se manda push.");
}

const db = new pg.Pool({
  connectionString: DATABASE_URL,
  max: 3,
  ssl: { rejectUnauthorized: false },
});

async function main() {
  // 1. Avisos pendientes: episodio nuevo × seguidor sin notificación previa
  const { rows: pending } = await db.query(
    `SELECT f.user_id,
            s.slug          AS series_slug,
            s.title         AS series_title,
            s.cover_url,
            e.episode_number
     FROM episodes e
     JOIN series s          ON s.id = e.series_id
     JOIN series_follows f  ON f.series_slug = s.slug AND f.notify
     WHERE e.is_published
       AND e.created_at > now() - ($1 || ' hours')::interval
       AND NOT EXISTS (
         SELECT 1 FROM user_notifications n
         WHERE n.user_id = f.user_id
           AND n.type = 'new_episode'
           AND n.url = '/series/' || s.slug || '/' || e.episode_number
       )
     ORDER BY e.created_at DESC
     LIMIT 2000`,
    [LOOKBACK_HOURS],
  );

  console.log(`Avisos pendientes: ${pending.length}`);
  if (pending.length === 0) return;

  // 2. Suscripciones push de los usuarios involucrados
  const userIds = [...new Set(pending.map((p) => p.user_id))];
  const { rows: subs } = await db.query(
    `SELECT user_id, endpoint, p256dh, auth FROM push_subscriptions
     WHERE user_id = ANY($1)`,
    [userIds],
  );
  const subsByUser = new Map();
  for (const s of subs) {
    if (!subsByUser.has(s.user_id)) subsByUser.set(s.user_id, []);
    subsByUser.get(s.user_id).push(s);
  }

  let inApp = 0;
  let pushed = 0;
  let deadSubs = 0;

  for (const p of pending) {
    const url = `/series/${p.series_slug}/${p.episode_number}`;
    const title = p.series_title;
    const body = `Salió el episodio ${p.episode_number} — entrá a verlo`;

    // Notificación in-app (la campana del header la va a leer de acá)
    await db.query(
      `INSERT INTO user_notifications (user_id, type, title, body, url)
       VALUES ($1, 'new_episode', $2, $3, $4)`,
      [p.user_id, title, body, url],
    );
    inApp++;

    // Web Push a cada navegador suscripto del usuario
    if (!pushEnabled) continue;
    for (const sub of subsByUser.get(p.user_id) ?? []) {
      try {
        await webpush.sendNotification(
          { endpoint: sub.endpoint, keys: { p256dh: sub.p256dh, auth: sub.auth } },
          JSON.stringify({
            title,
            body,
            url: `${SITE_URL}${url}`,
            icon: p.cover_url || `${SITE_URL}/favicon.png`,
          }),
        );
        pushed++;
      } catch (err) {
        // 404/410 = suscripción muerta (navegador desinstalado) → limpiar
        if (err?.statusCode === 404 || err?.statusCode === 410) {
          await db.query(`DELETE FROM push_subscriptions WHERE endpoint = $1`, [sub.endpoint]);
          deadSubs++;
        } else {
          console.warn(`Push falló (${err?.statusCode ?? "?"}):`, err?.message ?? err);
        }
      }
    }
  }

  console.log(`In-app: ${inApp} · Push enviados: ${pushed} · Suscripciones muertas limpiadas: ${deadSubs}`);
}

main()
  .catch((err) => {
    console.error(err);
    process.exitCode = 1;
  })
  .finally(() => db.end());
