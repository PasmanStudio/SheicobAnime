import AdSlot from "@/components/ads/AdSlot";
import InactivityAdTrigger from "@/components/ads/InactivityAdTrigger";
import ContinueWatchingRow from "@/components/ui/ContinueWatchingRow";
import HeroCarousel from "@/components/ui/HeroCarousel";
import RecentEpisodes from "@/components/ui/RecentEpisodes";
import { getRecentEpisodes, getSeries } from "@/lib/api";
import type { Episode, PaginatedResponse, Series } from "@/lib/types";
import type { Metadata } from "next";
import Image from "next/image";
import Link from "next/link";

// ISR: regenerate at most once per minute. Public data (series, episodes)
// doesn't need to be fresh on every request — 60s staleness is acceptable.
export const revalidate = 60;

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
      getRecentEpisodes({ days: 3, pageSize: 30 }),
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

      {/* Continuar viendo — client-side, hidden if no progress */}
      <ContinueWatchingRow />

      {/* Recent Episodes — grouped by day (JKAnime "Programación" style) */}
      <section>
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-xl font-bold text-white">📅 Últimos Episodios</h2>
        </div>
        <RecentEpisodes episodes={recentEpisodes} />
      </section>

      <AdSlot placement="home_mid" />

      {/* Top Series — ranked by score, JKAnime-style horizontal scroll */}
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
        <div className="flex gap-3 overflow-x-auto pb-3 scrollbar-thin scrollbar-track-neutral-800 scrollbar-thumb-neutral-600">
          {topRated.data.map((s, i) => (
            <Link
              key={s.id}
              href={`/series/${s.slug}`}
              className="group relative flex-shrink-0 w-56 rounded-lg overflow-hidden"
            >
              {/* Background image — 16:9 */}
              <div className="relative aspect-[16/9] bg-neutral-700 overflow-hidden">
                {s.coverUrl ? (
                  <Image
                    src={s.coverUrl}
                    alt={s.title}
                    fill
                    sizes="224px"
                    className="object-cover group-hover:scale-105 transition-transform duration-300"
                  />
                ) : (
                  <div className="w-full h-full bg-neutral-700" />
                )}
                {/* Dark gradient overlay */}
                <div className="absolute inset-0 bg-gradient-to-t from-black/90 via-black/40 to-transparent" />
                {/* Rank badge */}
                <span className="absolute top-2 left-2 bg-amber-500 text-black text-xs font-extrabold w-7 h-7 flex items-center justify-center rounded-md shadow-lg">
                  #{i + 1}
                </span>
                {/* Title on image */}
                <div className="absolute bottom-0 left-0 right-0 p-2.5">
                  <p className="text-sm font-semibold text-white line-clamp-2 leading-snug drop-shadow-lg">
                    {s.title}
                  </p>
                </div>
              </div>
            </Link>
          ))}
        </div>
      </section>

      <AdSlot placement="home_bottom" />
    </div>
  );
}
