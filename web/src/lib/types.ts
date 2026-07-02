// ─── Enums / Unions ──────────────────────────────────

export type SeriesStatus = "ongoing" | "completed" | "upcoming" | "hiatus";
export type SeriesType = "tv" | "movie" | "ova" | "ona" | "special";
export type ScrapeJobStatus = "pending" | "running" | "completed" | "failed" | "dead_letter";

// ─── Core entities ───────────────────────────────────

export interface Series {
  id: string;
  slug: string;
  title: string;
  titleRomaji: string | null;
  titleNative: string | null;
  synopsis: string | null;
  coverUrl: string | null;
  bannerUrl: string | null;
  year: number | null;
  status: SeriesStatus | null;
  type: SeriesType | null;
  score: number | null;
  episodeCount: number | null;
  studio: string | null;
  season: string | null;
  demographics: string | null;
  language: string | null;
  durationMinutes: number | null;
  airedDate: string | null;
  quality: string | null;
  genres: Genre[];
  createdAt: string;
  updatedAt: string;
}

export interface Episode {
  id: string;
  seriesId: string;
  episodeNumber: number;
  title: string | null;
  thumbnailUrl: string | null;
  durationSecs: number | null;
  airedAt: string | null;
  isPublished: boolean;
  createdAt: string;
  imdbId?: string | null;
  imdbRating?: number | null;
  imdbVotes?: number | null;
  series?: SeriesStub;
  mirrors?: Mirror[];
}

export interface SeriesStub {
  id: string;
  slug: string;
  title: string;
  coverUrl: string | null;
  imdbId?: string | null;
}

/** Native (our own) per-episode star rating aggregate + this device's own vote. */
export interface EpisodeRatingStats {
  average: number;
  count: number;
  myRating: number | null;
}

export interface SeriesSuggest {
  slug: string;
  title: string;
  coverUrl: string | null;
  type: SeriesType | null;
  status: SeriesStatus | null;
}

export interface Mirror {
  id: string;
  episodeId: string;
  providerName: string;
  embedUrl: string;
  qualityLabel: number;
  priority: number;
  isActive: boolean;
}

export interface Genre {
  id: number;
  name: string;
}

// ─── Watch progress ──────────────────────────────────

export interface WatchProgress {
  episodeId: string;
  seriesSlug: string;
  positionSeconds: number;
  durationSeconds: number;
  completed: boolean;
  updatedAt: string;
}

export interface RecentProgress extends WatchProgress {
  seriesTitle: string | null;
  seriesCoverUrl: string | null;
  episodeNumber: number;
  episodeTitle: string | null;
}

/** Serie pendiente de seguir: terminaste un episodio y ya salió el siguiente. */
export interface PendingSeries {
  seriesSlug: string;
  seriesTitle: string | null;
  seriesCoverUrl: string | null;
  lastWatchedEpisode: number;
  nextEpisodeNumber: number;
  updatedAt: string;
}

// ─── API response shapes ─────────────────────────────

export interface PaginatedResponse<T> {
  data: T[];
  total: number;
  page: number;
  pageSize: number;
}

/** Entrada mínima de /episodes/sitemap — solo lo que necesita el XML. */
export interface EpisodeSitemapEntry {
  seriesSlug: string;
  episodeNumber: number;
  createdAt: string;
}

export interface ApiErrorResponse {
  error: string;
  code: string;
  details?: Record<string, unknown>;
}

export interface HealthResponse {
  status: "healthy" | "degraded";
  db: string;
  cache: string;
  version: string;
}

// ─── Query params ────────────────────────────────────

export interface SeriesQueryParams {
  page?: number;
  pageSize?: number;
  status?: SeriesStatus;
  type?: SeriesType;
  genre?: string;
  year?: number;
  letter?: string;
  sort?: "score" | "updated" | "year" | "title" | "title_desc";
}

export interface SearchQueryParams {
  q: string;
  page?: number;
  pageSize?: number;
}

export interface EpisodeQueryParams {
  page?: number;
  pageSize?: number;
}

// ─── Admin types ─────────────────────────────────────

export interface ScrapeRequest {
  sourceUrl: string;
  forceRefresh?: boolean;
}

export interface ScrapeJobResponse {
  jobId: string;
  status: "queued";
}

export interface ScrapeJob {
  id: string;
  seriesId: string | null;
  jobType: string;
  status: ScrapeJobStatus;
  attemptCount: number;
  errorMessage: string | null;
  scheduledAt: string;
  completedAt: string | null;
}
