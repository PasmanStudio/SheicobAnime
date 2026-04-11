import type { Metadata } from "next";
import { getGenres } from "@/lib/api";

export const dynamic = "force-dynamic";
import Link from "next/link";
import AdSlot from "@/components/ads/AdSlot";

export const metadata: Metadata = {
  title: "All Genres",
  description: "Browse anime by genre on SheicobAnime.",
};

export default async function GenresIndexPage() {
  const genres = await getGenres();

  return (
    <div className="container mx-auto px-4 py-8 space-y-6">
      <AdSlot placement="genres_top" />

      <h1 className="text-2xl font-bold text-white">Browse by Genre</h1>

      <div className="flex flex-wrap gap-3">
        {genres.map((g) => (
          <Link
            key={g.id}
            href={`/genres/${encodeURIComponent(g.name)}`}
            className="px-4 py-2 rounded-full border border-indigo-700/60 text-indigo-300 hover:bg-indigo-700/20 hover:border-indigo-500 transition-colors text-sm font-medium"
          >
            {g.name}
          </Link>
        ))}
      </div>
    </div>
  );
}
