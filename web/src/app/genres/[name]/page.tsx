import type { Metadata } from "next";
import { getSeries } from "@/lib/api";

export const dynamic = "force-dynamic";
import SeriesCard from "@/components/ui/SeriesCard";
import Pagination from "@/components/ui/Pagination";
import AdSlot from "@/components/ads/AdSlot";
import Link from "next/link";

interface Props {
  params: { name: string };
  searchParams: { page?: string };
}

export function generateMetadata({ params }: Pick<Props, "params">): Metadata {
  const name = decodeURIComponent(params.name);
  return {
    title: `${name} Anime`,
    description: `Browse ${name} anime series on SheicobAnime.`,
  };
}

export default async function GenrePage({ params, searchParams }: Props) {
  const name = decodeURIComponent(params.name);
  const page = Math.max(1, parseInt(searchParams.page ?? "1", 10));

  const results = await getSeries({ genre: name, page, pageSize: 24 });

  return (
    <div className="container mx-auto px-4 py-8 space-y-6">
      <AdSlot placement="genre_top" />

      {/* Breadcrumb */}
      <nav className="text-sm text-neutral-500 flex items-center gap-2">
        <Link href="/genres" className="hover:text-white transition-colors">
          Genres
        </Link>
        <span>/</span>
        <span className="text-neutral-300">{name}</span>
      </nav>

      <div>
        <h1 className="text-2xl font-bold text-white">{name} Anime</h1>
        <p className="text-sm text-neutral-500 mt-1">
          {results.total} series
        </p>
      </div>

      {results.data.length === 0 ? (
        <p className="text-neutral-400 py-8 text-center">
          No series found for this genre.
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
        basePath={`/genres/${params.name}`}
      />

      <AdSlot placement="genre_bottom" />
    </div>
  );
}
