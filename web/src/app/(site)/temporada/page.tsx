import AdSlot from "@/components/ads/AdSlot";
import SeasonCard from "@/components/ui/SeasonCard";
import {
  getCurrentSeason,
  getSeasonalAnime,
  getSeasonNav,
  SEASON_EMOJI,
  SEASON_LABELS,
  SEASON_ORDER,
  titlesMatch,
  type AniListSeason,
} from "@/lib/anilist";
import { getSeries } from "@/lib/api";
import type { Metadata } from "next";
import Link from "next/link";

// ISR: revalidate every hour (AniList data changes daily, our DB changes daily)
export const revalidate = 3600;

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

  // Fetch AniList seasonal data + our indexed series in parallel.
  // We query both `year` and `year-1` because multi-cour series often start in the
  // previous year (e.g. Re:Zero S4 started 2024 but airs in spring 2025).
  // pageSize 300 ensures we cover all indexed series without pagination gaps.
  const fallback = { data: [] as import("@/lib/types").Series[], total: 0, page: 1, pageSize: 300 };
  const [anilistData, ourSeriesCurrent, ourSeriesPrev] = await Promise.all([
    getSeasonalAnime(season, year),
    getSeries({ year, pageSize: 300, sort: "score" }).catch(() => fallback),
    getSeries({ year: year - 1, pageSize: 300, sort: "score" }).catch(() => fallback),
  ]);

  // Merge both year results (deduplicate by slug)
  const seenSlugs = new Set<string>();
  const allSeries = [...ourSeriesCurrent.data, ...ourSeriesPrev.data].filter((s) => {
    if (seenSlugs.has(s.slug)) return false;
    seenSlugs.add(s.slug);
    return true;
  });

  // Match each AniList entry against our DB
  const matched = anilistData.map((media) => {
    const found =
      allSeries.find((s) =>
        titlesMatch(media, s.title, s.titleRomaji, s.titleNative),
      ) ?? null;
    return { media, match: found };
  });

  const availableCount = matched.filter((m) => m.match !== null).length;
  const seasonNav = getSeasonNav(year);

  // Year navigation (previous/next)
  const prevYear = year - 1;
  const nextYear = year + 1;
  const isCurrentYear = year === currentYear;

  return (
    <div className="container mx-auto px-4 py-8 space-y-6">
      {/* Header */}
      <div className="flex flex-col sm:flex-row sm:items-end justify-between gap-3">
        <div>
          <h1 className="text-2xl font-bold text-white">
            {SEASON_EMOJI[season]} Temporada — {SEASON_LABELS[season]} {year}
          </h1>
          <p className="text-sm text-neutral-400 mt-1">
            {availableCount} de {anilistData.length} títulos disponibles en SheicobAnime
          </p>
        </div>

        {/* Year navigation */}
        <div className="flex items-center gap-2 text-sm">
          <Link
            href={`/temporada?season=${season}&year=${prevYear}`}
            className="px-3 py-1.5 rounded-md bg-neutral-800 text-neutral-400 hover:text-white hover:bg-neutral-700 transition-colors"
          >
            ← {prevYear}
          </Link>
          <span className="px-3 py-1.5 rounded-md bg-neutral-800 text-white font-semibold">
            {year}
          </span>
          {!isCurrentYear && (
            <Link
              href={`/temporada?season=${season}&year=${nextYear}`}
              className="px-3 py-1.5 rounded-md bg-neutral-800 text-neutral-400 hover:text-white hover:bg-neutral-700 transition-colors"
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
              className={`flex items-center gap-1.5 px-4 py-2 rounded-full text-sm font-medium whitespace-nowrap transition-colors ${
                isActive
                  ? "bg-indigo-600 text-white"
                  : "bg-neutral-800 text-neutral-400 hover:text-white hover:bg-neutral-700"
              }`}
            >
              {SEASON_EMOJI[s]}
              {SEASON_LABELS[s]}
              {isCurrent && (
                <span className="w-1.5 h-1.5 rounded-full bg-green-400 inline-block" />
              )}
            </Link>
          );
        })}
      </div>

      <AdSlot placement="directory_top" />

      {/* Grid */}
      {anilistData.length === 0 ? (
        <div className="text-center py-20 text-neutral-400">
          <p className="text-lg">No se encontraron datos para esta temporada.</p>
          <p className="text-sm mt-2">Puede que AniList aún no tenga información disponible.</p>
        </div>
      ) : (
        <>
          {/* Legend */}
          <div className="flex items-center gap-4 text-xs text-neutral-500">
            <span className="flex items-center gap-1.5">
              <span className="inline-block w-2.5 h-2.5 rounded-sm bg-green-600" />
              Disponible en SheicobAnime
            </span>
            <span className="flex items-center gap-1.5">
              <span className="inline-block w-2.5 h-2.5 rounded-sm bg-neutral-600" />
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
