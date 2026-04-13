import AdSlot from "@/components/ads/AdSlot";
import InactivityAdTrigger from "@/components/ads/InactivityAdTrigger";
import HeroCarousel from "@/components/ui/HeroCarousel";
import RecentEpisodes from "@/components/ui/RecentEpisodes";
import SeriesCard from "@/components/ui/SeriesCard";
import { getRecentEpisodes, getSeries } from "@/lib/api";
import type { Episode, PaginatedResponse, Series } from "@/lib/types";
import type { Metadata } from "next";
import Link from "next/link";

export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "SheicobAnime — Watch Anime Online",
  description: "Discover and watch the latest anime episodes online. Updated daily.",
};

export default async function HomePage() {
  let recent: PaginatedResponse<Series> = { data: [], total: 0, page: 1, pageSize: 12 };
  let topRated: PaginatedResponse<Series> = { data: [], total: 0, page: 1, pageSize: 12 };
  let recentEpisodes: Episode[] = [];

  try {
    [recent, topRated, recentEpisodes] = await Promise.all([
      getSeries({ sort: "updated", pageSize: 12 }),
      getSeries({ sort: "score", pageSize: 12 }),
      getRecentEpisodes({ days: 3, pageSize: 50 }),
    ]);
  } catch (error) {
    console.error("Failed to fetch data for homepage:", error);
  }

  return (
    <div className="container mx-auto px-4 py-6 space-y-10">
      <InactivityAdTrigger />
      {/* Hero Carousel */}
      {recent.data.length > 0 && (
        <HeroCarousel series={recent.data} />
      )}

      <AdSlot placement="home_top" />

      {/* Recent Episodes — grouped by day (JKAnime "Programación" style) */}
      <section>
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-xl font-bold text-white">📅 Últimos Episodios</h2>
        </div>
        <RecentEpisodes episodes={recentEpisodes} />
      </section>

      <AdSlot placement="home_mid" />

      {/* Top Series — ranked by score */}
      <section>
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-xl font-bold text-white">🏆 Top Animes</h2>
          <Link
            href="/directory?sort=score"
            className="text-sm text-indigo-400 hover:text-indigo-300 transition-colors"
          >
            Ver todo →
          </Link>
        </div>
        <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 gap-4">
          {topRated.data.map((s, i) => (
            <div key={s.id} className="relative">
              {/* Rank badge */}
              <span className="absolute -top-2 -left-2 z-10 bg-amber-500 text-black text-xs font-bold w-7 h-7 flex items-center justify-center rounded-full shadow-lg">
                #{i + 1}
              </span>
              <SeriesCard series={s} />
            </div>
          ))}
        </div>
      </section>

      <AdSlot placement="home_bottom" />
    </div>
  );
}
