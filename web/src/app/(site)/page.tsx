import AdSlot from "@/components/ads/AdSlot";
import InactivityAdTrigger from "@/components/ads/InactivityAdTrigger";
import ContinueWatchingRow from "@/components/ui/ContinueWatchingRow";
import GenreChip from "@/components/ui/GenreChip";
import HeroCarousel from "@/components/ui/HeroCarousel";
import PendingSeriesRow from "@/components/ui/PendingSeriesRow";
import RankNumber from "@/components/ui/RankNumber";
import RecentEpisodes from "@/components/ui/RecentEpisodes";
import SectionHeader from "@/components/ui/SectionHeader";
import SeriesCard from "@/components/ui/SeriesCard";
import { getGenres, getRecentEpisodes, getSeries } from "@/lib/api";
import type { Episode, Genre, PaginatedResponse, Series } from "@/lib/types";
import type { Metadata } from "next";

// force-dynamic: skip static pre-render at build time.
// The home page calls the external API (Render) which can be cold at build.
// Vercel CDN / Next.js route cache handles caching at the edge.
export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "SheicobAnime — Mirá anime online en español",
  description:
    "Mirá los últimos episodios de anime online, sub español. Catálogo actualizado todos los días.",
};

export default async function HomePage() {
  let recent: PaginatedResponse<Series> = { data: [], total: 0, page: 1, pageSize: 12 };
  let topRated: PaginatedResponse<Series> = { data: [], total: 0, page: 1, pageSize: 12 };
  let recentEpisodes: Episode[] = [];
  let genres: Genre[] = [];

  try {
    [recent, topRated, recentEpisodes, genres] = await Promise.all([
      getSeries({ sort: "updated", pageSize: 12 }),
      getSeries({ sort: "score", pageSize: 10 }),
      getRecentEpisodes({ days: 3, pageSize: 30 }),
      getGenres().catch(() => [] as Genre[]),
    ]);
  } catch (error) {
    console.error("Failed to fetch data for homepage:", error);
  }

  return (
    <div className="mx-auto max-w-container px-4 py-6 space-y-10">
      <InactivityAdTrigger />

      {/* Hero */}
      {recent.data.length > 0 && <HeroCarousel series={recent.data} />}

      {/* Continuar viendo (episodios a medio ver) — primero, client-side */}
      <ContinueWatchingRow />

      {/* Seguir mirando (series con el siguiente episodio pendiente) */}
      <PendingSeriesRow />

      {/* Últimos episodios */}
      <section>
        <SectionHeader
          title="Últimos episodios"
          eyebrow="Actualizado todos los días"
          className="mb-4"
        />
        <RecentEpisodes episodes={recentEpisodes} />
      </section>

      <AdSlot placement="home_mid" />

      {/* Top 10 */}
      <section>
        <SectionHeader
          title="Top 10 de la comunidad"
          action="Ranking completo"
          actionHref="/ranking"
          className="mb-4"
        />
        <div className="sh-scroll-row items-end pb-1.5">
          {topRated.data.slice(0, 10).map((s, i) => (
            <div key={s.id} className="flex items-end shrink-0">
              <RankNumber
                rank={i + 1}
                size="lg"
                className="z-10 -mr-3.5 mb-2"
                style={{ textShadow: "0 2px 8px rgba(0,0,0,0.8)" }}
              />
              <SeriesCard series={s} className="w-[150px]" />
            </div>
          ))}
        </div>
      </section>

      {/* Explorar géneros */}
      {genres.length > 0 && (
        <section>
          <SectionHeader title="Explorá por género" className="mb-4" />
          <div className="flex flex-wrap gap-2">
            {genres.slice(0, 16).map((g) => (
              <GenreChip key={g.id} name={g.name} />
            ))}
          </div>
        </section>
      )}

      <AdSlot placement="home_bottom" />
    </div>
  );
}
