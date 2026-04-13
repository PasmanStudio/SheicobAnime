import type {
    Episode,
    EpisodeQueryParams,
    Genre,
    HealthResponse,
    Mirror,
    PaginatedResponse,
    SearchQueryParams,
    Series,
    SeriesQueryParams,
    SeriesSuggest,
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

async function request<T>(
  path: string,
  options: RequestInit = {}
): Promise<T> {
  const url = `${API_BASE_URL}${path}`;

  const res = await fetch(url, {
    ...options,
    cache: "no-store",
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
  return request<HealthResponse>("/health");
}

export async function getSeries(
  params: SeriesQueryParams = {}
): Promise<PaginatedResponse<Series>> {
  return request<PaginatedResponse<Series>>(
    `/series${toQueryString({ ...params })}`
  );
}

export async function searchSeries(
  params: SearchQueryParams
): Promise<PaginatedResponse<Series>> {
  return request<PaginatedResponse<Series>>(
    `/series/search${toQueryString({ ...params })}`
  );
}

export async function suggestSeries(q: string): Promise<SeriesSuggest[]> {
  return request<SeriesSuggest[]>(
    `/series/suggest${toQueryString({ q })}`
  );
}

export async function getSeriesBySlug(slug: string): Promise<Series> {
  return request<Series>(`/series/${encodeURIComponent(slug)}`);
}

export async function getSeriesEpisodes(
  slug: string,
  params: EpisodeQueryParams = {}
): Promise<PaginatedResponse<Episode>> {
  return request<PaginatedResponse<Episode>>(
    `/series/${encodeURIComponent(slug)}/episodes${toQueryString({ ...params })}`
  );
}

export async function getEpisode(id: string): Promise<Episode> {
  return request<Episode>(`/episodes/${encodeURIComponent(id)}`);
}

export async function getRecentEpisodes(
  params: { days?: number; pageSize?: number } = {}
): Promise<Episode[]> {
  return request<Episode[]>(
    `/episodes/recent${toQueryString({ ...params })}`
  );
}

export async function getEpisodeMirrors(id: string): Promise<Mirror[]> {
  return request<Mirror[]>(
    `/episodes/${encodeURIComponent(id)}/mirrors`
  );
}

export async function reportMirrorFailure(id: string): Promise<void> {
  // Fire-and-forget — never block UI on this call
  await request<void>(`/mirrors/${encodeURIComponent(id)}/report`, {
    method: "PATCH",
  });
}

export async function getGenres(): Promise<Genre[]> {
  return request<Genre[]>("/genres");
}
