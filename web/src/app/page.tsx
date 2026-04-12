import AdSlot from "@/components/ads/AdSlot";
import SeriesCard from "@/components/ui/SeriesCard";
import { getSeries } from "@/lib/api";
import type { PaginatedResponse, Series } from "@/lib/types";
import type { Metadata } from "next";

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
    <div className="container mx-auto px-4 py-8 space-y-10">
      <AdSlot placement="home_top" />

      <section>
        <h2 className="text-xl font-bold text-white mb-4">Recently Updated</h2>
        <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 gap-4">
          {recent.data.map((s) => (
            <SeriesCard key={s.id} series={s} />
          ))}
        </div>
      </section>

      <AdSlot placement="home_mid" />

      <section>
        <h2 className="text-xl font-bold text-white mb-4">Top Rated</h2>
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
