import type { Metadata } from "next";
import { getSeries } from "@/lib/api";

export const dynamic = "force-dynamic";
import SeriesCard from "@/components/ui/SeriesCard";
import AdSlot from "@/components/ads/AdSlot";

export const metadata: Metadata = {
  title: "SheicobAnime — Watch Anime Online",
  description: "Discover and watch the latest anime episodes online. Updated daily.",
};

export default async function HomePage() {
  const [recent, topRated] = await Promise.all([
    getSeries({ sort: "updated", pageSize: 12 }),
    getSeries({ sort: "score", pageSize: 12 }),
  ]);

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
