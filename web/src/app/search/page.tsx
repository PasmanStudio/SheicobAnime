import type { Metadata } from "next";
import { searchSeries } from "@/lib/api";

export const dynamic = "force-dynamic";
import SeriesCard from "@/components/ui/SeriesCard";
import Pagination from "@/components/ui/Pagination";
import AdSlot from "@/components/ads/AdSlot";

interface Props {
  searchParams: { q?: string; page?: string };
}

export function generateMetadata({ searchParams }: Props): Metadata {
  const q = searchParams.q?.trim() ?? "";
  return {
    title: q ? `Search: ${q}` : "Search Anime",
    description: q
      ? `Search results for "${q}" on SheicobAnime.`
      : "Search for anime series on SheicobAnime.",
  };
}

export default async function SearchPage({ searchParams }: Props) {
  const q = searchParams.q?.trim() ?? "";
  const page = Math.max(1, parseInt(searchParams.page ?? "1", 10));

  if (!q) {
    return (
      <div className="container mx-auto px-4 py-16 text-center space-y-4">
        <h1 className="text-2xl font-bold text-white">Search Anime</h1>
        <p className="text-neutral-400">
          Use the search bar above to find anime series.
        </p>
      </div>
    );
  }

  let results;
  try {
    results = await searchSeries({ q, page, pageSize: 24 });
  } catch {
    results = { data: [], total: 0, page: 1, pageSize: 24 };
  }

  return (
    <div className="container mx-auto px-4 py-8 space-y-6">
      <AdSlot placement="search_top" />

      <div>
        <h1 className="text-xl font-bold text-white">
          Results for{" "}
          <span className="text-indigo-400">&ldquo;{q}&rdquo;</span>
        </h1>
        <p className="text-sm text-neutral-500 mt-1">
          {results.total} series found
        </p>
      </div>

      {results.data.length === 0 ? (
        <p className="text-neutral-400 py-8 text-center">
          No results for &ldquo;{q}&rdquo;. Try a different search term.
        </p>
      ) : (
        <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 gap-4">
          {results.data.map((s) => (
            <SeriesCard key={s.id} series={s} />
          ))}
        </div>
      )}

      <Pagination
        page={page}
        total={results.total}
        pageSize={24}
        basePath={`/search?q=${encodeURIComponent(q)}`}
      />

      <AdSlot placement="search_bottom" />
    </div>
  );
}
