import type {
    Episode,
    EpisodeQueryParams,
    Genre,
    HealthResponse,
    Mirror,
    PaginatedResponse,
    PendingSeries,
  RecentProgress,
    ResolvableMirror,
    ResolvedSource,
    SearchQueryParams,
    Series,
    SeriesQueryParams,
    SeriesSuggest,
    WatchProgress,
} from "./types";

// ─── Configuration ───────────────────────────────────

const API_BASE_URL =
  process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000";

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
    { next: { revalidate: 300 } }
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
    { next: { revalidate: 300 } }
  );
}

export async function getSeriesEpisodes(
  slug: string,
  params: EpisodeQueryParams = {}
): Promise<PaginatedResponse<Episode>> {
  return request<PaginatedResponse<Episode>>(
    `/series/${encodeURIComponent(slug)}/episodes${toQueryString({ ...params })}`,
    { next: { revalidate: 300 } }
  );
}

export async function getEpisode(id: string): Promise<Episode> {
  return request<Episode>(
    `/episodes/${encodeURIComponent(id)}`,
    { next: { revalidate: 300 } }
  );
}

export async function getEpisodeBySlug(slug: string, episodeNumber: number): Promise<Episode> {
  return request<Episode>(
    `/series/${encodeURIComponent(slug)}/episodes/${episodeNumber}`,
    { next: { revalidate: 300 } }
  );
}

export async function getRecentEpisodes(
  params: { days?: number; pageSize?: number } = {}
): Promise<Episode[]> {
  // Recent episodes change often — short TTL so the homepage stays fresh.
  return request<Episode[]>(
    `/episodes/recent${toQueryString({ ...params })}`,
    { next: { revalidate: 120 } }
  );
}

export async function getEpisodeMirrors(id: string): Promise<Mirror[]> {
  // Mirrors are stable once uploaded — cache aggressively.
  return request<Mirror[]>(
    `/episodes/${encodeURIComponent(id)}/mirrors`,
    { next: { revalidate: 3600 } }
  );
}

export async function getEpisodeMirrorsBySlug(slug: string, episodeNumber: number): Promise<Mirror[]> {
  // Mirrors are stable once uploaded — cache aggressively.
  return request<Mirror[]>(
    `/series/${encodeURIComponent(slug)}/episodes/${episodeNumber}/mirrors`,
    { next: { revalidate: 3600 } }
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

// ─── Resolver (Sheicob) ──────────────────────────────

/**
 * Asks the API to extract the actual video URL (m3u8/mp4) for a mirror.
 * Returns 501 if hoster is unsupported, 503 if extraction failed, 410 if blocked.
 */
export async function resolveMirror(mirrorId: string): Promise<ResolvedSource> {
  return request<ResolvedSource>(`/mirrors/${encodeURIComponent(mirrorId)}/resolve`, {
    method: "POST",
    cache: "no-store", // resolved URLs are short-lived — never cache
  });
}

/**
 * Returns the list of mirrors for an episode with `resolvable=true` for those whose
 * hoster is supported by the resolver registry — used to render Sheicob-branded buttons.
 */
export async function getResolvableSet(episodeId: string): Promise<ResolvableMirror[]> {
  return request<ResolvableMirror[]>(
    `/mirrors/${encodeURIComponent(episodeId)}/resolvable-set`,
    { next: { revalidate: 3600 } }
  );
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
