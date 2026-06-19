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

// El API corre en Render free-tier: tras un deploy se enfría y el primer
// request tarda ~50s en despertar. Sin timeout, un fetch del home colgaba el
// Worker y la página renderizaba vacía. Abortamos a los 12s para degradar con
// gracia (el ISR sirve la última copia buena; ver open-next.config.ts).
const FETCH_TIMEOUT_MS = 12_000;

// Cache de CONTENIDO que cambia con cada scrape (series/episodios/mirrors).
// - `revalidate: 60` → piso de frescura: contenido nuevo visible en ≤1 min.
//   (En Next, el revalidate más bajo entre página y sus fetches manda, así que
//   esto baja la regeneración de toda la ruta a 60s sin tocar cada page.tsx.)
// - `tags: ["content"]` → revalidación ON-DEMAND: el scraper pega a /api/revalidate
//   al terminar un run y purga este tag → contenido nuevo al instante. Requiere el
//   tag cache KV (NEXT_TAG_CACHE_KV en wrangler.jsonc + open-next.config.ts). Sin
//   ese binding el tag es no-op y queda solo el TTL de 60s. Degrada con gracia.
const CONTENT_CACHE: { next: NextFetchRequestConfig } = {
  next: { revalidate: 60, tags: ["content"] },
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

// ─── Base fetch wrapper ──────────────────────────────

/**
 * Public/server-side fetch — NO credentials flag so Next.js can share-cache the
 * response across all visitors.  Caller controls the `next.revalidate` TTL.
 */
async function request<T>(
  path: string,
  options: RequestInit & { next?: NextFetchRequestConfig } = {}
): Promise<T> {
  const url = `${API_BASE_URL}${path}`;

  const res = await fetch(url, {
    ...options,
    signal: AbortSignal.timeout(FETCH_TIMEOUT_MS),
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
    signal: AbortSignal.timeout(FETCH_TIMEOUT_MS),
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

export async function searchSeries(
  params: SearchQueryParams
): Promise<PaginatedResponse<Series>> {
  // Search results are dynamic — short cache so new series appear quickly.
  return request<PaginatedResponse<Series>>(
    `/series/search${toQueryString({ ...params })}`,
    { next: { revalidate: 60 } }
  );
}

export async function suggestSeries(q: string): Promise<SeriesSuggest[]> {
  // Autocomplete — always fetched client-side, revalidate irrelevant but set a short TTL.
  return request<SeriesSuggest[]>(
    `/series/suggest${toQueryString({ q })}`,
    { next: { revalidate: 30 } }
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
  // Los mirrors cambian cuando el scraper sube a hosts nuevos (player4me, etc.) —
  // por eso NO se cachean 1h: TTL 60s + tag "content" para refresco on-demand.
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
