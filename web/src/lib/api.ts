import type {
    Episode,
    EpisodeQueryParams,
    EpisodeRatingStats,
    Genre,
    HealthResponse,
    Mirror,
    PaginatedResponse,
    PendingSeries,
  RecentProgress,
    SearchQueryParams,
    Series,
    SeriesQueryParams,
    SeriesSuggest,
    WatchProgress,
} from "./types";

// ─── Configuration ───────────────────────────────────

const API_BASE_URL =
  process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000";

// El API corre en Render free-tier y DUERME la instancia tras ~15 min sin
// tráfico. Auditoría del 7-sep-2026: el servicio está arriba el 11% del tiempo y
// un cold start mide 20-36 s. El timeout único de 12 s que había acá era MENOR
// que el cold start, así que el primer visitante después de cada siesta se comía
// un abort garantizado → la sección caía en su `.catch(() => [])` → vacío.
//
// PERO: reintentar hasta absorber el cold start entero fue peor. Con un
// presupuesto de 8s+20s+20s el sitio se cayó con `error code: 1102` el
// 7-sep-2026 a las 23:14 UTC. Analytics de Cloudflare, hora 23:00:
// `exceededResources` 6 requests / 6 errores, con **wallTimeP99 = 40,6 s**
// (cpuP99 apenas 150 ms → el límite que se rompe es el de WALL TIME del Worker,
// no el de CPU). La hora anterior ya venía con wallTimeP99 de 47 s "exitosos":
// estaba al borde.
//
// La lección: **los reintentos NO son la defensa contra el cold start — el
// keep-alive lo es.** Acá solo cubren un hipo de red. El presupuesto total tiene
// que quedar bien por debajo del límite del Worker (~30 s) contando que una
// misma request puede encadenar más de un fetch, y además nadie va a esperar 50
// segundos una página igual. Si el API está frío, la respuesta correcta es
// fallar rápido hacia el cache, no hacer esperar al usuario.
//
// Presupuesto: 4 s (caso tibio) + 6 s (un reintento) + 300 ms de backoff ≈ 10,3 s
// por fetch. Los fetches de una página corren en paralelo, así que una página
// normal no pasa de ~10 s; la que encadena dos fases (temporada) usa la política
// best-effort en la segunda para no sumar dos presupuestos completos.
const RETRY_TIMEOUTS_MS = [4_000, 6_000];
const RETRY_BACKOFF_MS = [300];

// Camino interactivo (client components / route handlers): acá SÍ hay un humano
// esperando, así que un intento corto y a otra cosa.
const INTERACTIVE_TIMEOUT_MS = 12_000;

// Cache de CONTENIDO que cambia con cada scrape (series/episodios/mirrors).
// - `tags: ["content"]` → revalidación ON-DEMAND y mecanismo PRINCIPAL de frescura:
//   el scraper pega a /api/revalidate al terminar un run y purga este tag →
//   contenido nuevo al instante, sin esperar el TTL. Verificado en prod (log
//   "✅ Revalidate: tag 'content' purgado"). Requiere el tag cache KV
//   (NEXT_TAG_CACHE_KV en wrangler.jsonc + open-next.config.ts).
// - `revalidate: 3600` → TTL de RESPALDO (1h), no la vía de frescura. Solo
//   importa si la purga on-demand falla, o para ediciones manuales de la DB que
//   no disparan purga (aparecen dentro de 1h; forzables con POST a /api/revalidate).
//   Cada regeneración por TTL es un write a KV y el free tier son 1000 puts/día:
//   a 900s (15 min) los endpoints calientes del home (getRecentEpisodes, getSeries)
//   regeneraban ~96 veces/día CADA UNO → se agotaba la cuota → 429 en los puts →
//   ISR sin poder refrescar y el worker cayendo a renders pesados = Error 1102
//   (peor cerca de las 00:00 UTC, justo antes del reset diario). A 3600s eso baja
//   4× sin costo de frescura de contenido nuevo, porque la purga on-demand es
//   instantánea. Se puede subir más (7200/21600) si hace falta más margen.
//   (En Next, el revalidate más bajo entre página y sus fetches manda, así que
//   esto fija la regeneración de respaldo de toda la ruta sin tocar cada page.tsx.)
const CONTENT_CACHE: { next: NextFetchRequestConfig } = {
  next: { revalidate: 3600, tags: ["content"] },
};

// ─── Error class ─────────────────────────────────────

export class ApiError extends Error {
  constructor(
    public status: number,
    public code: string,
    message: string,
    public details?: Record<string, unknown>
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/**
 * El API no CONTESTÓ: timeout, error de red, o 5xx tras agotar los reintentos.
 *
 * Es distinto de un ApiError normal (404, 400): un 404 significa "esto no
 * existe" y renderizar vacío está bien; un ApiUnavailableError significa "no
 * sabemos qué hay" y renderizar vacío es MENTIR — y peor, esa mentira se
 * cachea. Las páginas usan esta distinción para decidir entre degradar en
 * silencio o dejar que el error suba (ver page.tsx / temporada/page.tsx).
 */
export class ApiUnavailableError extends ApiError {
  constructor(message: string, public attempts: number) {
    super(503, "API_UNAVAILABLE", message);
    this.name = "ApiUnavailableError";
  }
}

/** ¿Vale la pena reintentar? Un 404/400 no se arregla insistiendo. */
function isRetryableStatus(status: number): boolean {
  return status >= 500 || status === 429 || status === 408;
}

/**
 * Log estructurado de fallas del API.
 *
 * El Worker tiene `observability.enabled` en wrangler.jsonc → esto queda en
 * Workers Logs de Cloudflare y se puede filtrar por `event`. Era el agujero
 * grande de diagnóstico: cuando el home salía vacío no quedaba rastro en NINGÚN
 * lado (Sentry está apagado en el Worker vía DISABLE_SENTRY, y el API dormido
 * obviamente no reporta que está dormido).
 */
function logApiFailure(fields: Record<string, unknown>): void {
  try {
    console.error(JSON.stringify({ event: "api_fetch_failed", ...fields }));
  } catch {
    // nunca dejar que el logging rompa el render
  }
}

// ─── Base fetch wrapper ──────────────────────────────

/**
 * Public/server-side fetch — NO credentials flag so Next.js can share-cache the
 * response across all visitors.  Caller controls the `next.revalidate` TTL.
 */
async function request<T>(
  path: string,
  options: RequestInit & { next?: NextFetchRequestConfig } = {},
  timeouts: readonly number[] = RETRY_TIMEOUTS_MS
): Promise<T> {
  const url = `${API_BASE_URL}${path}`;
  const startedAt = Date.now();
  let lastReason = "unknown";

  // Reintentar SOLO métodos idempotentes.
  //
  // Un timeout no significa "no llegó": significa "no sabemos". Si el API
  // procesó el PATCH y lo que se perdió fue la respuesta, reintentar lo aplica
  // de nuevo. El caso concreto acá es `reportMirrorFailure` (PATCH a
  // /mirrors/{id}/report): son 3 intentos contra un contador de fallas, así que
  // un mirror sano podía sumar 3 fallas por UN corte de red y quedar degradado.
  // Los reintentos existen para el cold start de Render, que solo hace falta
  // absorber en lecturas.
  const method = (options.method ?? "GET").toUpperCase();
  const isIdempotent = method === "GET" || method === "HEAD";
  const effectiveTimeouts = isIdempotent ? timeouts : timeouts.slice(0, 1);

  for (let attempt = 0; attempt < effectiveTimeouts.length; attempt++) {
    try {
      const res = await fetch(url, {
        ...options,
        signal: AbortSignal.timeout(effectiveTimeouts[attempt]),
        headers: {
          "Content-Type": "application/json",
          ...options.headers,
        },
      });

      if (res.ok) return (await res.json()) as T;

      let errorBody: { error?: string; code?: string; details?: Record<string, unknown> } = {};
      try {
        errorBody = await res.json();
      } catch {
        // response body not JSON
      }

      // 502/503 acá es casi siempre el EDGE de Render contestando por una
      // instancia dormida o arrancando — el request ni llegó a la app. Ese es
      // exactamente el caso que hay que reintentar, porque el propio intento
      // fallido ya disparó el arranque y el siguiente suele encontrarla viva.
      if (isRetryableStatus(res.status) && attempt < effectiveTimeouts.length - 1) {
        lastReason = `http_${res.status}`;
        await sleep(RETRY_BACKOFF_MS[Math.min(attempt, RETRY_BACKOFF_MS.length - 1)]);
        continue;
      }

      if (isRetryableStatus(res.status)) {
        logApiFailure({
          path, method, reason: `http_${res.status}`,
          attempts: attempt + 1, elapsedMs: Date.now() - startedAt,
        });
        throw new ApiUnavailableError(
          `API no disponible (HTTP ${res.status}) tras ${attempt + 1} intentos`,
          attempt + 1
        );
      }

      // 4xx: respuesta legítima del API, no insistir.
      throw new ApiError(
        res.status,
        errorBody.code ?? "UNKNOWN",
        errorBody.error ?? `HTTP ${res.status}`,
        errorBody.details
      );
    } catch (err) {
      if (err instanceof ApiError) throw err;

      // Timeout (AbortSignal) o error de red. Ambos = "no contestó".
      lastReason = err instanceof Error && err.name === "TimeoutError" ? "timeout" : "network";
      if (attempt < effectiveTimeouts.length - 1) {
        await sleep(RETRY_BACKOFF_MS[Math.min(attempt, RETRY_BACKOFF_MS.length - 1)]);
        continue;
      }
      logApiFailure({
        path, method, reason: lastReason,
        attempts: attempt + 1, elapsedMs: Date.now() - startedAt,
        message: err instanceof Error ? err.message : String(err),
      });
      throw new ApiUnavailableError(
        `API no respondió (${lastReason}) tras ${attempt + 1} intentos`,
        attempt + 1
      );
    }
  }

  // Inalcanzable: el loop siempre sale por return o por throw.
  throw new ApiUnavailableError(`API no respondió (${lastReason})`, effectiveTimeouts.length);
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * User-specific fetch — sends the sheicob_did cookie so the API can identify the
 * device/user.  Uses `cache: 'no-store'` so Next.js never shares these responses
 * between users.  Call this only from client components or route handlers.
 */
async function requestWithCredentials<T>(
  path: string,
  options: RequestInit = {}
): Promise<T> {
  const url = `${API_BASE_URL}${path}`;

  const res = await fetch(url, {
    ...options,
    cache: "no-store",
    credentials: "include",
    signal: AbortSignal.timeout(INTERACTIVE_TIMEOUT_MS),
    headers: {
      "Content-Type": "application/json",
      ...options.headers,
    },
  });

  if (!res.ok) {
    let errorBody: { error?: string; code?: string; details?: Record<string, unknown> } = {};
    try {
      errorBody = await res.json();
    } catch {
      // response body not JSON
    }
    throw new ApiError(
      res.status,
      errorBody.code ?? "UNKNOWN",
      errorBody.error ?? `HTTP ${res.status}`,
      errorBody.details
    );
  }

  return res.json() as Promise<T>;
}

// ─── Query string builder ────────────────────────────

function toQueryString(params: Record<string, string | number | boolean | undefined | null>): string {
  const entries = Object.entries(params).filter(
    ([, v]) => v !== undefined && v !== null && v !== ""
  );
  if (entries.length === 0) return "";
  const search = new URLSearchParams(
    entries.map(([k, v]) => [k, String(v)])
  );
  return `?${search.toString()}`;
}

// ─── Public API ──────────────────────────────────────

export async function getHealth(): Promise<HealthResponse> {
  return request<HealthResponse>("/health", { next: { revalidate: 60 } });
}

export async function getSeries(
  params: SeriesQueryParams = {}
): Promise<PaginatedResponse<Series>> {
  return request<PaginatedResponse<Series>>(
    `/series${toQueryString({ ...params })}`,
    CONTENT_CACHE
  );
}

/**
 * Política para fetches OPCIONALES que se disparan de a muchos en paralelo
 * (el matcher por-título de /temporada). Un solo intento y corto: reintentar
 * 200 búsquedas contra un Render dormido multiplica el tiempo de render por 3
 * sin mejorar nada — si no matchean, la tarjeta ya cae en "No indexado aún".
 */
const BEST_EFFORT_TIMEOUTS_MS = [6_000];

/**
 * Listado "best effort": un intento corto, sin reintentos.
 *
 * Para la SEGUNDA fase de una página que ya gastó presupuesto en la primera
 * (el fallback al catálogo propio de /temporada). Encadenar dos presupuestos
 * completos es justamente lo que llevó el wall time del Worker a 40 s y lo hizo
 * exceder sus límites.
 */
export async function getSeriesFast(
  params: SeriesQueryParams = {}
): Promise<PaginatedResponse<Series>> {
  return request<PaginatedResponse<Series>>(
    `/series${toQueryString({ ...params })}`,
    CONTENT_CACHE,
    BEST_EFFORT_TIMEOUTS_MS
  );
}

/**
 * Búsqueda "best effort": un intento corto, sin reintentos.
 * Para el fan-out de /temporada, donde fallar es barato y esperar es caro.
 */
export async function searchSeriesFast(
  params: SearchQueryParams
): Promise<PaginatedResponse<Series>> {
  return request<PaginatedResponse<Series>>(
    `/series/search${toQueryString({ ...params })}`,
    { cache: "no-store" },
    BEST_EFFORT_TIMEOUTS_MS
  );
}

export async function searchSeries(
  params: SearchQueryParams
): Promise<PaginatedResponse<Series>> {
  // `no-store`: cada query es única (search page + el matcher por-título de
  // /temporada) → cachearla escribe una key nueva en KV por búsqueda, con hit
  // rate ~0 y re-write a cada TTL. Quema el free tier de KV (1000 puts/día) sin
  // beneficio real. Se sirve siempre fresco desde el API (acción del usuario).
  return request<PaginatedResponse<Series>>(
    `/series/search${toQueryString({ ...params })}`,
    { cache: "no-store" }
  );
}

export async function suggestSeries(q: string): Promise<SeriesSuggest[]> {
  // Autocomplete — `no-store`: query única por tecleo, no tiene sentido cachear
  // (además se llama client-side, donde el data cache de Next ni aplica).
  return request<SeriesSuggest[]>(
    `/series/suggest${toQueryString({ q })}`,
    { cache: "no-store" }
  );
}

export async function getSeriesBySlug(slug: string): Promise<Series> {
  return request<Series>(
    `/series/${encodeURIComponent(slug)}`,
    CONTENT_CACHE
  );
}

export async function getSeriesEpisodes(
  slug: string,
  params: EpisodeQueryParams = {}
): Promise<PaginatedResponse<Episode>> {
  return request<PaginatedResponse<Episode>>(
    `/series/${encodeURIComponent(slug)}/episodes${toQueryString({ ...params })}`,
    CONTENT_CACHE
  );
}

export async function getEpisode(id: string): Promise<Episode> {
  return request<Episode>(
    `/episodes/${encodeURIComponent(id)}`,
    CONTENT_CACHE
  );
}

export async function getEpisodeBySlug(slug: string, episodeNumber: number): Promise<Episode> {
  return request<Episode>(
    `/series/${encodeURIComponent(slug)}/episodes/${episodeNumber}`,
    CONTENT_CACHE
  );
}

export async function getRecentEpisodes(
  params: { days?: number; pageSize?: number } = {}
): Promise<Episode[]> {
  // Recent episodes change often — short TTL + tag para refresco on-demand del home.
  return request<Episode[]>(
    `/episodes/recent${toQueryString({ ...params })}`,
    CONTENT_CACHE
  );
}

export async function getEpisodeMirrorsBySlug(slug: string, episodeNumber: number): Promise<Mirror[]> {
  // Los mirrors cambian cuando el scraper sube a hosts nuevos (player4me, etc.).
  // Usan CONTENT_CACHE: el tag "content" los refresca on-demand al instante cuando
  // el scraper purga; el TTL de respaldo (1h) solo aplica a cambios sin purga.
  return request<Mirror[]>(
    `/series/${encodeURIComponent(slug)}/episodes/${episodeNumber}/mirrors`,
    CONTENT_CACHE
  );
}

export async function reportMirrorFailure(id: string): Promise<void> {
  // Fire-and-forget — never block UI on this call
  await request<void>(`/mirrors/${encodeURIComponent(id)}/report`, {
    method: "PATCH",
    cache: "no-store",
  });
}

export async function getGenres(): Promise<Genre[]> {
  return request<Genre[]>("/genres", { next: { revalidate: 3600 } });
}

// ─── Watch progress (user-specific — never cache) ────

export async function getWatchProgress(episodeId: string): Promise<WatchProgress | null> {
  try {
    return await requestWithCredentials<WatchProgress>(
      `/progress/${encodeURIComponent(episodeId)}`
    );
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) return null;
    throw err;
  }
}

export async function updateWatchProgress(
  episodeId: string,
  positionSeconds: number,
  durationSeconds: number
): Promise<void> {
  await requestWithCredentials<void>(`/progress/${encodeURIComponent(episodeId)}`, {
    method: "PUT",
    body: JSON.stringify({ positionSeconds, durationSeconds }),
  });
}

// ─── Native episode rating (our own star vote, by device id) ──────────

export async function getEpisodeRating(episodeId: string): Promise<EpisodeRatingStats> {
  return requestWithCredentials<EpisodeRatingStats>(
    `/episodes/${encodeURIComponent(episodeId)}/rating`
  );
}

export async function submitEpisodeRating(
  episodeId: string,
  rating: number
): Promise<EpisodeRatingStats> {
  return requestWithCredentials<EpisodeRatingStats>(
    `/episodes/${encodeURIComponent(episodeId)}/rating`,
    { method: "POST", body: JSON.stringify({ rating }) }
  );
}

export async function getRecentProgress(limit = 20): Promise<RecentProgress[]> {
  return requestWithCredentials<RecentProgress[]>(
    `/progress/recent${toQueryString({ limit })}`
  );
}

export async function getPendingSeries(limit = 12): Promise<PendingSeries[]> {
  return requestWithCredentials<PendingSeries[]>(
    `/progress/pending${toQueryString({ limit })}`
  );
}
