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

// ─── GraphQL query ─────────────────────────────────────────────────────────────

const SEASONAL_QUERY = /* GraphQL */ `
  query SeasonalAnime($season: MediaSeason, $seasonYear: Int) {
    Page(page: 1, perPage: 50) {
      media(
        season: $season
        seasonYear: $seasonYear
        type: ANIME
        sort: POPULARITY_DESC
        format_in: [TV, TV_SHORT, ONA]
        isAdult: false
      ) {
        id
        title { romaji english native }
        coverImage { extraLarge large color }
        bannerImage
        status
        episodes
        duration
        genres
        averageScore
        popularity
        startDate { year month day }
        studios(isMain: true) { nodes { name } }
        format
      }
    }
  }
`;

export async function getSeasonalAnime(
  season: AniListSeason,
  year: number,
): Promise<AniListMedia[]> {
  try {
    const res = await fetch("https://graphql.anilist.co", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Accept: "application/json",
      },
      body: JSON.stringify({
        query: SEASONAL_QUERY,
        variables: { season, seasonYear: year },
      }),
      next: { revalidate: 3600 }, // cache 1 h — AniList data changes daily
    });

    if (!res.ok) {
      console.error(`AniList API error: ${res.status} ${res.statusText}`);
      return [];
    }

    const json = await res.json();
    return (json?.data?.Page?.media ?? []) as AniListMedia[];
  } catch (err) {
    console.error("AniList fetch failed:", err);
    return [];
  }
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
