import AdSlot from "@/components/ads/AdSlot";
import SeasonCard from "@/components/ui/SeasonCard";
import {
  getCurrentSeason,
  getSeasonalAnime,
  getSeasonNav,
  SEASON_LABELS,
  SEASON_ORDER,
  titlesMatch,
  type AniListSeason,
} from "@/lib/anilist";
import { getSeries, searchSeries } from "@/lib/api";
import type { Series } from "@/lib/types";
import type { Metadata } from "next";
import Link from "next/link";

// force-dynamic: en Cloudflare Workers no hay incremental cache (ver
// open-next.config.ts), así que `revalidate` congelaba el HTML del build —
// si AniList fallaba en CI, la página quedaba vacía para siempre.
// El fetch a AniList se cachea aparte (next.revalidate en lib/anilist).
export const dynamic = "force-dynamic";

interface Props {
  searchParams: Promise<{ season?: string; year?: string }>;
}

export async function generateMetadata({ searchParams }: Props): Promise<Metadata> {
  const sp = await searchParams;
  const { season, year } = resolveParams(sp);
  const seasonLabel = SEASON_LABELS[season];
  return {
    title: `Temporada ${seasonLabel} ${year} — SheicobAnime`,
    description: `Anime de la temporada ${seasonLabel.toLowerCase()} ${year}. Descubrí qué títulos están disponibles en SheicobAnime.`,
  };
}

function resolveParams(sp: { season?: string; year?: string }): {
  season: AniListSeason;
  year: number;
} {
  const { season: defaultSeason, year: defaultYear } = getCurrentSeason();
  const season =
    sp.season && (SEASON_ORDER as string[]).includes(sp.season)
      ? (sp.season as AniListSeason)
      : defaultSeason;
  const year = sp.year ? parseInt(sp.year, 10) || defaultYear : defaultYear;
  return { season, year };
}

export default async function TemporadaPage({ searchParams }: Props) {
  const sp = await searchParams;
  const { season, year } = resolveParams(sp);
  const { season: currentSeason, year: currentYear } = getCurrentSeason();

  // ── Phase 1: broad pre-fetch in parallel ─────────────────────────────────────
  //
  // Two catalogue fetches to cover the most common cases:
  //   1. status=ongoing — all currently airing series (~87). New seasonal entries
  //      have score=null so they're invisible in score-sorted queries.
  //   2. sort=score&pageSize=500 — top 500 by score. Covers popular completed
  //      series when browsing past seasons.
  //
  const fallback = { data: [] as Series[], total: 0, page: 1, pageSize: 500 };
  const [anilistData, ongoingResult, topByScoreResult] = await Promise.all([
    getSeasonalAnime(season, year),
    getSeries({ pageSize: 500, status: "ongoing" }).catch(() => fallback),
    getSeries({ pageSize: 500, sort: "score" }).catch(() => fallback),
  ]);

  // Merge and deduplicate by slug (ongoing takes priority)
  const seenSlugs = new Set<string>();
  const prefetched = [...ongoingResult.data, ...topByScoreResult.data].filter((s) => {
    if (seenSlugs.has(s.slug)) return false;
    seenSlugs.add(s.slug);
    return true;
  });

  // ── Phase 2: first-pass title matching ────────────────────────────────────────
  const firstPass = anilistData.map((media) => ({
    media,
    match: prefetched.find((s) => titlesMatch(media, s.title, s.titleRomaji, s.titleNative)) ?? null,
  }));

  // ── Phase 3: search fallback for unmatched entries ────────────────────────────
  //
  // Many indexed series are status=completed with score=null (e.g. recently
  // finished shows), so they don't appear in either pre-fetch. For each AniList
  // entry still unmatched, we run a targeted search against our DB. This runs
  // server-to-server (low latency) and all searches fire in parallel.
  //
  const unmatched = firstPass.filter((m) => m.match === null);
  const fallbackMap = new Map<number, Series>(); // AniList media.id → matched Series

  if (unmatched.length > 0) {
    const searches = await Promise.all(
      unmatched.map(async ({ media }) => {
        const query = media.title.english ?? media.title.romaji;
        if (!query) return null;
        const results = await searchSeries({ q: query, pageSize: 5 }).catch(
          () => ({ data: [] as Series[] }),
        );
        const found = results.data.find((s) =>
          titlesMatch(media, s.title, s.titleRomaji, s.titleNative),
        );
        return found ? { id: media.id, series: found } : null;
      }),
    );

    for (const hit of searches) {
      if (hit) fallbackMap.set(hit.id, hit.series);
    }
  }

  // ── Final: combine both passes ────────────────────────────────────────────────
  const matched = firstPass.map((m) =>
    m.match !== null
      ? m
      : { media: m.media, match: fallbackMap.get(m.media.id) ?? null },
  );

  const availableCount = matched.filter((m) => m.match !== null).length;
  const seasonNav = getSeasonNav(year);

  // Year navigation (previous/next)
  const prevYear = year - 1;
  const nextYear = year + 1;
  const isCurrentYear = year === currentYear;

  return (
    <div className="mx-auto max-w-container px-4 py-8 space-y-6">
      {/* Header */}
      <div className="flex flex-col sm:flex-row sm:items-end justify-between gap-3">
        <div className="flex flex-col gap-1">
          <span className="sh-label">
            {availableCount} de {anilistData.length} títulos disponibles
          </span>
          <span className="sh-section-header items-center">
            <span className="sh-cut" />
            <h1 className="sh-display text-[clamp(22px,3vw,28px)]">
              {SEASON_LABELS[season]} {year}
            </h1>
          </span>
        </div>

        {/* Year navigation */}
        <div className="flex items-center gap-2 text-sm">
          <Link
            href={`/temporada?season=${season}&year=${prevYear}`}
            className="sh-stat px-3 py-1.5 rounded-btn bg-abyss-2 border border-line-1 text-xs text-ink-3 hover:text-ink-1 hover:border-line-2 transition-colors duration-fast"
          >
            ← {prevYear}
          </Link>
          <span className="sh-stat px-3 py-1.5 rounded-btn bg-abyss-3 border border-line-2 text-xs text-ink-1">
            {year}
          </span>
          {!isCurrentYear && (
            <Link
              href={`/temporada?season=${season}&year=${nextYear}`}
              className="sh-stat px-3 py-1.5 rounded-btn bg-abyss-2 border border-line-1 text-xs text-ink-3 hover:text-ink-1 hover:border-line-2 transition-colors duration-fast"
            >
              {nextYear} →
            </Link>
          )}
        </div>
      </div>

      {/* Season tabs */}
      <div className="flex gap-2 overflow-x-auto pb-1">
        {seasonNav.map(({ season: s }) => {
          const isActive = s === season;
          const isCurrent = s === currentSeason && year === currentYear;
          return (
            <Link
              key={s}
              href={`/temporada?season=${s}&year=${year}`}
              className={`flex items-center gap-2 px-4 py-2 rounded-full text-sm font-semibold whitespace-nowrap border transition-colors duration-fast ${
                isActive
                  ? "bg-[var(--accent-muted)] text-brand-bright border-[var(--accent-border)]"
                  : "bg-abyss-2 text-ink-3 border-line-1 hover:text-ink-1 hover:border-line-2"
              }`}
            >
              {isActive && <span className="sh-cut !mr-0 !w-[3px] !h-3" />}
              {SEASON_LABELS[s]}
              {isCurrent && <span className="sh-live-dot !w-1.5 !h-1.5" />}
            </Link>
          );
        })}
      </div>

      {/* Grid */}
      {anilistData.length === 0 ? (
        <div className="text-center py-20 text-sm">
          <p className="text-ink-2">Todavía no hay información de esta temporada.</p>
          <p className="mt-1 text-ink-3">Los estrenos se cargan apenas se anuncian — volvé en unos días.</p>
        </div>
      ) : (
        <>
          {/* Legend */}
          <div className="flex items-center gap-4 text-xs text-ink-3">
            <span className="flex items-center gap-1.5">
              <span className="inline-block w-2.5 h-2.5 rounded-sm bg-[var(--success)]" />
              Disponible en SheicobAnime
            </span>
            <span className="flex items-center gap-1.5">
              <span className="inline-block w-2.5 h-2.5 rounded-sm bg-abyss-3 border border-line-2" />
              No indexado aún
            </span>
          </div>

          <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 gap-4">
            {matched.map(({ media, match }) => (
              <SeasonCard key={media.id} media={media} match={match} />
            ))}
          </div>
        </>
      )}

      <AdSlot placement="directory_bottom" />
    </div>
  );
}
