/**
 * Keep-alive del API de SheicobAnime en Render (plan free).
 *
 * Corre por Cron Trigger de Cloudflare (ver wrangler.jsonc). Su único trabajo
 * es que la instancia no llegue a los 15 min de inactividad que la duermen.
 *
 * Detalle que importa: cuando la instancia está dormida, el PRIMER request no
 * la despierta a tiempo — el edge de Render devuelve 502/503 mientras arranca
 * el contenedor, y el arranque medido tarda 20-36 s. Por eso el ping reintenta
 * en vez de darse por vencido: el intento fallido es justamente el que dispara
 * el arranque, y el siguiente la encuentra viva. El keep-alive viejo en GitHub
 * Actions hacía un `curl` único y contaba como "fallido" exactamente esos
 * despertares (2 de los últimos 22 runs terminaron en `curl: (22) ... 503`).
 */

const ATTEMPT_TIMEOUTS_MS = [10_000, 25_000, 25_000];
const BACKOFF_MS = [1_000, 3_000];

async function ping(url, timeoutMs) {
  const started = Date.now();
  const res = await fetch(url, {
    headers: { "User-Agent": "SheicobAnime-KeepAlive/2.0" },
    signal: AbortSignal.timeout(timeoutMs),
  });
  return { status: res.status, ok: res.ok, ms: Date.now() - started };
}

async function wake(baseUrl) {
  let last = null;
  for (let attempt = 0; attempt < ATTEMPT_TIMEOUTS_MS.length; attempt++) {
    try {
      const r = await ping(`${baseUrl}/health`, ATTEMPT_TIMEOUTS_MS[attempt]);
      last = r;
      if (r.ok) return { ...r, attempts: attempt + 1, coldStart: attempt > 0 };
    } catch (err) {
      last = { status: 0, ok: false, ms: -1, error: String(err && err.message) };
    }
    if (attempt < ATTEMPT_TIMEOUTS_MS.length - 1) {
      await new Promise((r) => setTimeout(r, BACKOFF_MS[attempt]));
    }
  }
  return { ...last, attempts: ATTEMPT_TIMEOUTS_MS.length, coldStart: true };
}

/**
 * Calienta las rutas calientes. Best-effort puro: si fallan no pasa nada, el
 * objetivo (proceso vivo) ya se cumplió en `wake`.
 */
async function warm(baseUrl, paths) {
  const results = {};
  await Promise.all(
    paths.map(async (path) => {
      try {
        const r = await ping(`${baseUrl}${path}`, 25_000);
        results[path] = `${r.status} in ${r.ms}ms`;
      } catch (err) {
        results[path] = `error: ${String(err && err.message)}`;
      }
    }),
  );
  return results;
}

async function run(env) {
  const baseUrl = (env.API_BASE_URL || "").replace(/\/$/, "");
  if (!baseUrl) {
    console.error(JSON.stringify({ event: "keepalive_misconfigured", reason: "API_BASE_URL vacío" }));
    return { ok: false, reason: "API_BASE_URL vacío" };
  }

  const health = await wake(baseUrl);
  const paths = (env.WARM_PATHS || "")
    .split(",")
    .map((p) => p.trim())
    .filter(Boolean);
  const warmed = health.ok && paths.length > 0 ? await warm(baseUrl, paths) : {};

  // Log estructurado → queda en Workers Logs (observability habilitado). Es la
  // serie temporal que antes no existía: permite ver desde afuera cuántos
  // despertares hubo y cuánto tarda realmente un cold start.
  const line = { event: "keepalive", ...health, warmed };
  if (health.ok) console.log(JSON.stringify(line));
  else console.error(JSON.stringify(line));

  return { ok: health.ok, health, warmed };
}

export default {
  async scheduled(event, env, ctx) {
    ctx.waitUntil(run(env));
  },

  // Invocación manual, para probar sin esperar al cron.
  //
  // Defensa en profundidad: `workers_dev` está en false, así que en condiciones
  // normales este handler no es alcanzable. Igual pide un secreto, porque una
  // ruta agregada por error en el dashboard no debería convertir esto en un
  // botón anónimo para quemar las horas de instancia de Render.
  //
  // Setear con: wrangler secret put KEEPALIVE_TRIGGER_SECRET
  // Llamar con: curl -H "x-keepalive-secret: <valor>" <url>
  async fetch(request, env) {
    const expected = env.KEEPALIVE_TRIGGER_SECRET;
    if (!expected || request.headers.get("x-keepalive-secret") !== expected) {
      return new Response("Not found", { status: 404 });
    }

    const result = await run(env);
    return new Response(JSON.stringify(result, null, 2), {
      status: result.ok ? 200 : 503,
      headers: { "Content-Type": "application/json" },
    });
  },
};
