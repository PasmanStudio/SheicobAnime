// ─── AniList public GraphQL API (no auth required) ────────────────────────────
// Docs: https://anilist.gitbook.io/anilist-apiv2-docs/

export type AniListSeason = "WINTER" | "SPRING" | "SUMMER" | "FALL";

export interface AniListMedia {
  id: number;
  title: {
    romaji: string | null;
    english: string | null;
    native: string | null;
  };
  coverImage: {
    extraLarge: string | null;
    large: string | null;
    color: string | null;
  };
  bannerImage: string | null;
  status: "FINISHED" | "RELEASING" | "NOT_YET_RELEASED" | "CANCELLED" | "HIATUS" | null;
  episodes: number | null;
  duration: number | null;
  genres: string[];
  averageScore: number | null;  // out of 100
  popularity: number | null;
  startDate: { year: number | null; month: number | null; day: number | null };
  studios: { nodes: { name: string }[] };
  format: "TV" | "TV_SHORT" | "MOVIE" | "SPECIAL" | "OVA" | "ONA" | "MUSIC" | null;
}

// ─── Season helpers ────────────────────────────────────────────────────────────

export const SEASON_ORDER: AniListSeason[] = ["WINTER", "SPRING", "SUMMER", "FALL"];

export const SEASON_LABELS: Record<AniListSeason, string> = {
  WINTER: "Invierno",
  SPRING: "Primavera",
  SUMMER: "Verano",
  FALL: "Otoño",
};

export const SEASON_EMOJI: Record<AniListSeason, string> = {
  WINTER: "❄️",
  SPRING: "🌸",
  SUMMER: "☀️",
  FALL: "🍂",
};

/** Map AniList season → Spanish name used by jkanime/our DB */
export const SEASON_ES: Record<AniListSeason, string> = {
  WINTER: "Invierno",
  SPRING: "Primavera",
  SUMMER: "Verano",
  FALL: "Otoño",
};

export function getCurrentSeason(): { season: AniListSeason; year: number } {
  const month = new Date().getMonth() + 1; // 1–12
  const year = new Date().getFullYear();
  let season: AniListSeason;
  if (month <= 3) season = "WINTER";
  else if (month <= 6) season = "SPRING";
  else if (month <= 9) season = "SUMMER";
  else season = "FALL";
  return { season, year };
}

/** Returns the 4 seasons for a given year as navigation items */
export function getSeasonNav(year: number): { season: AniListSeason; year: number; label: string }[] {
  return SEASON_ORDER.map((season) => ({
    season,
    year,
    label: `${SEASON_LABELS[season]} ${year}`,
  }));
}

const API_BASE_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000";

/**
 * Lanzado cuando NO pudimos averiguar qué hay en la temporada.
 *
 * Distinto de "la temporada está vacía": /temporada mostraba el mismo cartel
 * ("Todavía no hay información de esta temporada") en los dos casos, así que
 * cada siesta de Render se veía como una temporada sin estrenos.
 */
export class SeasonUnavailableError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "SeasonUnavailableError";
  }
}

export async function getSeasonalAnime(
  season: AniListSeason,
  year: number,
): Promise<AniListMedia[]> {
  // Vía el proxy del API (Render): el fetch directo a graphql.anilist.co desde
  // Cloudflare Workers falla intermitentemente (bot-protection de AniList contra
  // requests datacenter-a-datacenter). El proxy además cachea 6 h en Redis.
  //
  // ANTES: `cache: "no-store"` y sin timeout. Como /temporada es dinámica, CADA
  // visita pegaba en vivo a un Render que está dormido el 88% del tiempo → 503
  // → `return []` → "no hay información de esta temporada". Y al no cachearse
  // nada, no había copia buena de la cual caer.
  //
  // AHORA: se cachea el payload 6 h en el incremental cache (KV). El contenido
  // de una temporada cambia de a poco, así que 6 h es de sobra, y con eso la
  // página deja de depender de que Render esté despierto en ese instante.
  // Son ~4 keys por año navegado: no mueve la aguja del free tier de KV.
  const url = `${API_BASE_URL}/anilist/season/${encodeURIComponent(season)}/${year}`;
  const started = Date.now();

  // Mismo criterio que lib/api.ts: un intento corto para el caso tibio y dos
  // largos que le dan tiempo al cold start de Render (20-36 s medidos).
  const timeouts = [8_000, 20_000, 20_000];
  let reason = "unknown";

  for (let attempt = 0; attempt < timeouts.length; attempt++) {
    try {
      const res = await fetch(url, {
        signal: AbortSignal.timeout(timeouts[attempt]),
        next: { revalidate: 21_600, tags: ["season"] },
      });

      if (res.ok) {
        const json = await res.json();
        return (Array.isArray(json) ? json : []) as AniListMedia[];
      }

      reason = `http_${res.status}`;
      // 5xx/429 = Render dormido o arrancando → reintentar. 4xx = respuesta real.
      if (!(res.status >= 500 || res.status === 429 || res.status === 408)) break;
    } catch (err) {
      reason = err instanceof Error && err.name === "TimeoutError" ? "timeout" : "network";
    }

    if (attempt < timeouts.length - 1) {
      await new Promise((r) => setTimeout(r, attempt === 0 ? 500 : 1_500));
    }
  }

  console.error(
    JSON.stringify({
      event: "anilist_season_failed",
      season,
      year,
      reason,
      elapsedMs: Date.now() - started,
    }),
  );
  throw new SeasonUnavailableError(`No se pudo cargar la temporada ${season} ${year} (${reason})`);
}

// ─── Title matching ────────────────────────────────────────────────────────────

const COMBINING_MARKS_RE = /[̀-ͯ]/g;

function normalize(s: string): string {
  return s
    .toLowerCase()
    .normalize("NFD")
    .replace(COMBINING_MARKS_RE, "") // strip combining diacritical marks
    .replace(/[^a-z0-9\s]/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

/**
 * Returns true if any AniList title variant matches any of our DB title variants.
 * Uses substring inclusion so "Demon Slayer" matches "Demon Slayer: Kimetsu no Yaiba".
 */
export function titlesMatch(
  anilist: AniListMedia,
  ourTitle: string,
  ourTitleRomaji: string | null,
  ourTitleNative: string | null,
): boolean {
  // Normalize and filter: drop empty strings that result from titles that are
  // purely Japanese/CJK characters (they collapse to "" after normalization).
  // We use >= 2 (not 0 or 1) to skip empty/single-char noise while still
  // allowing short real titles like "Mao" (normalizes to "mao", length 3).
  // Substring matching has its own >= 12 guard below to prevent short-title
  // false positives in includes() checks.
  const candidates = [
    anilist.title.romaji,
    anilist.title.english,
    anilist.title.native,
  ]
    .filter((t): t is string => Boolean(t))
    .map(normalize)
    .filter((t) => t.length >= 2);

  const ours = [ourTitle, ourTitleRomaji, ourTitleNative]
    .filter((t): t is string => Boolean(t))
    .map(normalize)
    .filter((t) => t.length >= 2);

  if (candidates.length === 0 || ours.length === 0) return false;

  return candidates.some((c) =>
    ours.some(
      (o) =>
        c === o ||
        (c.length >= 4 && o.includes(c)) ||
        (o.length >= 4 && c.includes(o)),
    ),
  );
}

// ─── Format helpers ────────────────────────────────────────────────────────────

export function formatAniListFormat(format: AniListMedia["format"]): string {
  switch (format) {
    case "TV":       return "Serie";
    case "TV_SHORT": return "Short";
    case "ONA":      return "ONA";
    case "MOVIE":    return "Película";
    case "OVA":      return "OVA";
    case "SPECIAL":  return "Especial";
    default:         return "Anime";
  }
}

export function formatScore(score: number | null): string | null {
  if (score === null || score === 0) return null;
  return (score / 10).toFixed(1);
}
