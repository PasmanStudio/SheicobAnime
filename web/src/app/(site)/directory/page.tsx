import AdSlot from "@/components/ads/AdSlot";
import InactivityAdTrigger from "@/components/ads/InactivityAdTrigger";
import DirectoryFilters from "@/components/ui/DirectoryFilters";
import Pagination from "@/components/ui/Pagination";
import SeriesCard from "@/components/ui/SeriesCard";
import { getSeries } from "@/lib/api";
import type { SeriesQueryParams, SeriesStatus, SeriesType } from "@/lib/types";
import type { Metadata } from "next";

export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "Directorio de Anime",
  description: "Explora el directorio completo de anime. Filtra por género, tipo, estado, año y más.",
};

interface Props {
  searchParams: {
    genre?: string;
    letter?: string;
    type?: string;
    status?: string;
    year?: string;
    sort?: string;
    page?: string;
  };
}

export default async function DirectoryPage({ searchParams }: Props) {
  const page = Math.max(1, parseInt(searchParams.page ?? "1", 10));
  const pageSize = 24;

  const params: SeriesQueryParams = {
    page,
    pageSize,
    sort: (searchParams.sort as SeriesQueryParams["sort"]) ?? "updated",
  };

  if (searchParams.genre) params.genre = searchParams.genre;
  if (searchParams.type) params.type = searchParams.type as SeriesType;
  if (searchParams.status) params.status = searchParams.status as SeriesStatus;
  if (searchParams.year) params.year = parseInt(searchParams.year, 10);
  if (searchParams.letter) params.letter = searchParams.letter;

  let results;
  try {
    results = await getSeries(params);
  } catch {
    results = { data: [], total: 0, page: 1, pageSize };
  }

  // Build basePath for pagination preserving current filters
  const filterParams = new URLSearchParams();
  if (searchParams.genre) filterParams.set("genre", searchParams.genre);
  if (searchParams.letter) filterParams.set("letter", searchParams.letter);
  if (searchParams.type) filterParams.set("type", searchParams.type);
  if (searchParams.status) filterParams.set("status", searchParams.status);
  if (searchParams.year) filterParams.set("year", searchParams.year);
  if (searchParams.sort) filterParams.set("sort", searchParams.sort);
  const filterString = filterParams.toString();
  const basePath = filterString ? `/directory?${filterString}` : "/directory";

  return (
    <div className="container mx-auto px-4 py-8 space-y-6">
      <InactivityAdTrigger />
      <div>
        <h1 className="text-2xl font-bold text-white">Directorio de Anime</h1>
        <p className="text-sm text-neutral-400 mt-1">
          {results.total.toLocaleString()} anime encontrados
        </p>
      </div>

      <DirectoryFilters
        currentFilters={{
          genre: searchParams.genre,
          letter: searchParams.letter,
          type: searchParams.type,
          status: searchParams.status,
          year: searchParams.year,
          sort: searchParams.sort,
        }}
      />

      <AdSlot placement="directory_top" />

      {results.data.length === 0 ? (
        <p className="text-neutral-400 py-12 text-center">
          No se encontraron resultados con los filtros seleccionados.
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
        pageSize={pageSize}
        basePath={basePath}
      />

      <AdSlot placement="directory_bottom" />
    </div>
  );
}
