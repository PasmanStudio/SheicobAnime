import AdSlot from "@/components/ads/AdSlot";
import InactivityAdTrigger from "@/components/ads/InactivityAdTrigger";
import HeroCarousel from "@/components/ui/HeroCarousel";
import SeriesCard from "@/components/ui/SeriesCard";
import { getSeries } from "@/lib/api";
import type { PaginatedResponse, Series } from "@/lib/types";
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

  try {
    [recent, topRated] = await Promise.all([
      getSeries({ sort: "updated", pageSize: 12 }),
      getSeries({ sort: "score", pageSize: 12 }),
    ]);
  } catch (error) {
    console.error("Failed to fetch series for homepage:", error);
  }

  return (
    <div className="container mx-auto px-4 py-6 space-y-10">
      <InactivityAdTrigger />
      {/* Hero Carousel */}
      {recent.data.length > 0 && (
        <HeroCarousel series={recent.data} />
      )}

      <AdSlot placement="home_top" />

      <section>
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-xl font-bold text-white">Últimos Actualizados</h2>
          <Link
            href="/directory?sort=updated"
            className="text-sm text-indigo-400 hover:text-indigo-300 transition-colors"
          >
            Ver todos →
          </Link>
        </div>
        <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 gap-4">
          {recent.data.map((s) => (
            <SeriesCard key={s.id} series={s} />
          ))}
        </div>
      </section>

      <AdSlot placement="home_mid" />

      <section>
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-xl font-bold text-white">Más Populares</h2>
          <Link
            href="/directory?sort=score"
            className="text-sm text-indigo-400 hover:text-indigo-300 transition-colors"
          >
            Ver todos →
          </Link>
        </div>
        <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 gap-4">
          {topRated.data.map((s) => (
            <SeriesCard key={s.id} series={s} />
          ))}
        </div>
      </section>

      <AdSlot placement="home_bottom" />
    </div>
  );
}
