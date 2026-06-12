// Digest semanal por email (doc 3, fase 3) — domingos 18:00 ART.
//
// Para cada usuario con email_digest_opt_in: episodios de la semana de sus
// series seguidas + top 5 de la semana por likes. Un click para desuscribir
// (link con email_unsub_token). Envía vía Resend (https://resend.com).
//
// Env requerido:
//   DATABASE_URL     — postgres:// URI
//   RESEND_API_KEY   — API key de Resend
//   DIGEST_FROM      — remitente verificado en Resend (ej: "SheicobAnime <hola@sheicobanime.com>")
//   SITE_URL         — opcional, default https://sheicobanime.sheicob.workers.dev

import { randomBytes } from "node:crypto";
import pg from "pg";

const DATABASE_URL = process.env.DATABASE_URL;
const RESEND_API_KEY = process.env.RESEND_API_KEY;
const DIGEST_FROM = process.env.DIGEST_FROM;
const SITE_URL = (process.env.SITE_URL ?? "https://sheicobanime.sheicob.workers.dev").replace(/\/$/, "");

if (!DATABASE_URL) {
  console.error("Falta DATABASE_URL");
  process.exit(1);
}
if (!RESEND_API_KEY || !DIGEST_FROM) {
  console.warn("Sin RESEND_API_KEY / DIGEST_FROM — no hay nada que enviar. Saliendo.");
  process.exit(0);
}

const db = new pg.Pool({
  connectionString: DATABASE_URL,
  max: 3,
  ssl: { rejectUnauthorized: false },
});

const esc = (s) =>
  String(s ?? "").replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

function buildHtml({ name, episodes, top, unsubUrl }) {
  const epRows = episodes
    .map(
      (e) => `
      <tr>
        <td style="padding:8px 0;border-bottom:1px solid #1C2434;">
          <a href="${SITE_URL}/series/${e.series_slug}/${e.episode_number}"
             style="color:#6EDDFF;text-decoration:none;font-weight:600;">
            ${esc(e.series_title)}
          </a>
          <span style="color:#97A3B8;"> — episodio ${e.episode_number}</span>
        </td>
      </tr>`,
    )
    .join("");

  const topRows = top
    .map(
      (t, i) => `
      <tr>
        <td style="padding:6px 0;color:${i === 0 ? "#FFC53D" : "#97A3B8"};font-family:monospace;width:36px;">#${i + 1}</td>
        <td style="padding:6px 0;">
          <a href="${SITE_URL}/series/${t.series_slug}" style="color:#F2F6FB;text-decoration:none;">
            ${esc(t.series_title)}
          </a>
          <span style="color:#5B6678;font-family:monospace;font-size:12px;"> ♥ ${t.like_count}</span>
        </td>
      </tr>`,
    )
    .join("");

  return `
  <div style="background:#07090E;color:#F2F6FB;font-family:system-ui,sans-serif;padding:32px 24px;max-width:560px;margin:0 auto;">
    <p style="color:#6EDDFF;font-family:monospace;font-size:11px;letter-spacing:2px;text-transform:uppercase;margin:0 0 4px;">
      Tu semana en SheicobAnime
    </p>
    <h1 style="font-style:italic;text-transform:uppercase;font-size:22px;margin:0 0 20px;">
      Hola${name ? ` ${esc(name)}` : ""} — esto te perdiste
    </h1>

    ${
      episodes.length > 0
        ? `<h2 style="font-size:14px;color:#97A3B8;margin:20px 0 6px;">Episodios nuevos de tus series</h2>
           <table style="width:100%;border-collapse:collapse;font-size:14px;">${epRows}</table>`
        : `<p style="color:#97A3B8;font-size:14px;">Esta semana no salieron episodios de tus series seguidas.</p>`
    }

    ${
      top.length > 0
        ? `<h2 style="font-size:14px;color:#97A3B8;margin:24px 0 6px;">El top de la semana</h2>
           <table style="width:100%;border-collapse:collapse;font-size:14px;">${topRows}</table>`
        : ""
    }

    <p style="margin:28px 0 0;">
      <a href="${SITE_URL}" style="display:inline-block;background:#14B1E7;color:#04121B;font-weight:700;padding:10px 20px;border-radius:10px;text-decoration:none;">
        Seguir mirando
      </a>
    </p>

    <p style="color:#5B6678;font-size:11px;margin-top:32px;">
      Recibís este correo porque activaste el digest semanal.
      <a href="${unsubUrl}" style="color:#5B6678;">Desuscribirme</a> — un click y listo.
    </p>
  </div>`;
}

async function sendEmail({ to, subject, html }) {
  const res = await fetch("https://api.resend.com/emails", {
    method: "POST",
    headers: {
      Authorization: `Bearer ${RESEND_API_KEY}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({ from: DIGEST_FROM, to: [to], subject, html }),
  });
  if (!res.ok) {
    throw new Error(`Resend ${res.status}: ${await res.text()}`);
  }
}

async function main() {
  // Suscriptos al digest (asegura token de desuscripción)
  const { rows: users } = await db.query(
    `SELECT id::text AS id, name, email, email_unsub_token
     FROM users
     WHERE email_digest_opt_in AND email IS NOT NULL`,
  );
  console.log(`Suscriptos al digest: ${users.length}`);
  if (users.length === 0) return;

  // Top de la semana por likes (compartido entre todos los emails).
  // user_anime_likes se creó a mano en Supabase — si no tiene created_at,
  // caemos al top histórico.
  let top;
  try {
    ({ rows: top } = await db.query(
      `SELECT series_slug, series_title, COUNT(*)::int AS like_count
       FROM user_anime_likes
       WHERE created_at > now() - interval '7 days'
       GROUP BY series_slug, series_title
       ORDER BY like_count DESC
       LIMIT 5`,
    ));
  } catch {
    ({ rows: top } = await db.query(
      `SELECT series_slug, series_title, COUNT(*)::int AS like_count
       FROM user_anime_likes
       GROUP BY series_slug, series_title
       ORDER BY like_count DESC
       LIMIT 5`,
    ));
  }

  let sent = 0;
  for (const u of users) {
    // Token de desuscripción (se genera la primera vez)
    let token = u.email_unsub_token;
    if (!token) {
      token = randomBytes(24).toString("hex");
      await db.query(`UPDATE users SET email_unsub_token = $1 WHERE id::text = $2`, [token, u.id]);
    }

    // Episodios de la semana de SUS series seguidas
    const { rows: episodes } = await db.query(
      `SELECT s.slug AS series_slug, s.title AS series_title, e.episode_number
       FROM episodes e
       JOIN series s         ON s.id = e.series_id
       JOIN series_follows f ON f.series_slug = s.slug AND f.notify AND f.user_id = $1
       WHERE e.is_published AND e.created_at > now() - interval '7 days'
       ORDER BY e.created_at DESC
       LIMIT 20`,
      [u.id],
    );

    // Sin contenido propio NI top → no mandar nada (cero spam)
    if (episodes.length === 0 && top.length === 0) continue;

    const unsubUrl = `${SITE_URL}/api/digest/unsubscribe?token=${token}`;
    try {
      await sendEmail({
        to: u.email,
        subject:
          episodes.length > 0
            ? `${episodes.length} episodio${episodes.length !== 1 ? "s" : ""} nuevo${episodes.length !== 1 ? "s" : ""} de tus series`
            : "El top de la semana en SheicobAnime",
        html: buildHtml({ name: u.name, episodes, top, unsubUrl }),
      });
      sent++;
      // Resend free tier: rate limit suave
      await new Promise((r) => setTimeout(r, 600));
    } catch (err) {
      console.warn(`Email a ${u.email} falló:`, err.message);
    }
  }

  console.log(`Digests enviados: ${sent}`);
}

main()
  .catch((err) => {
    console.error(err);
    process.exitCode = 1;
  })
  .finally(() => db.end());
