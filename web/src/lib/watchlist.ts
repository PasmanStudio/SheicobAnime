// Shared types and server-side helpers for watchlist + history.

export type WatchStatus = "mirando" | "visto" | "por_ver" | "favorito" | "dropped";

export const WATCH_STATUSES: WatchStatus[] = ["mirando", "visto", "por_ver", "favorito", "dropped"];

export const WATCH_STATUS_LABELS: Record<WatchStatus, string> = {
  mirando:  "Mirando",
  visto:    "Visto",
  por_ver:  "Por ver",
  favorito: "Favorito",
  dropped:  "Dropped",
};

export const WATCH_STATUS_ICONS: Record<WatchStatus, string> = {
  mirando:  "▶",
  visto:    "✓",
  por_ver:  "⌚",
  favorito: "♥",
  dropped:  "✕",
};

export const WATCH_STATUS_COLORS: Record<WatchStatus, string> = {
  mirando:  "text-blue-400 border-blue-700/50",
  visto:    "text-green-400 border-green-700/50",
  por_ver:  "text-yellow-400 border-yellow-700/50",
  favorito: "text-pink-400 border-pink-700/50",
  dropped:  "text-neutral-400 border-neutral-700/50",
};

export interface WatchEntry {
  user_id: string;
  series_slug: string;
  series_title: string;
  cover_url: string | null;
  status: WatchStatus;
  created_at: string;
  updated_at: string;
}

export interface EpisodeHistoryEntry {
  user_id: string;
  episode_id: string;
  series_slug: string;
  episode_number: number;
  episode_title: string | null;
  series_title: string;
  cover_url: string | null;
  watched_at: string;
}
