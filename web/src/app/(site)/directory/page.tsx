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
  searchParams: Promise<{
    genre?: string;
    letter?: string;
    type?: string;
    status?: string;
    year?: string;
    sort?: string;
    page?: string;
  }>;
}

export default async function DirectoryPage({ searchParams }: Props) {
  const sp = await searchParams;
  const page = Math.max(1, parseInt(sp.page ?? "1", 10));
  const pageSize = 24;

  const params: SeriesQueryParams = {
    page,
    pageSize,
    sort: (sp.sort as SeriesQueryParams["sort"]) ?? "updated",
  };

  if (sp.genre) params.genre = sp.genre;
  if (sp.type) params.type = sp.type as SeriesType;
  if (sp.status) params.status = sp.status as SeriesStatus;
  if (sp.year) params.year = parseInt(sp.year, 10);
  if (sp.letter) params.letter = sp.letter;

  const hasFilters = Boolean(
    sp.genre || sp.letter || sp.type || sp.status || sp.year || (sp.sort && sp.sort !== "updated")
  );

  let results;
  try {
    results = await getSeries(params);
  } catch {
    results = { data: [], total: 0, page: 1, pageSize };
  }

  // Build basePath for pagination preserving current filters
  const filterParams = new URLSearchParams();
  if (sp.genre) filterParams.set("genre", sp.genre);
  if (sp.letter) filterParams.set("letter", sp.letter);
  if (sp.type) filterParams.set("type", sp.type);
  if (sp.status) filterParams.set("status", sp.status);
  if (sp.year) filterParams.set("year", sp.year);
  if (sp.sort) filterParams.set("sort", sp.sort);
  const filterString = filterParams.toString();
  const basePath = filterString ? `/directory?${filterString}` : "/directory";

  return (
    <div className="mx-auto max-w-container px-4 py-8 space-y-6">
      <InactivityAdTrigger />
      <div className="flex flex-col gap-1">
        <span className="sh-label">
          {results.total.toLocaleString("es-AR")} {hasFilters ? "resultados" : "anime en el catálogo"}
        </span>
        <span className="sh-section-header items-center">
          <span className="sh-cut" />
          <h1 className="sh-display text-[clamp(22px,3vw,28px)]">Directorio</h1>
        </span>
      </div>

      <DirectoryFilters
        currentFilters={{
          genre: sp.genre,
          letter: sp.letter,
          type: sp.type,
          status: sp.status,
          year: sp.year,
          sort: sp.sort,
        }}
      />

      <AdSlot placement="directory_top" />

      {results.data.length === 0 ? (
        <div className="py-12 text-center text-sm">
          <p className="text-ink-2">No se encontraron resultados con esos filtros.</p>
          <p className="mt-1 text-ink-3">Prueba sacando alguno — el catálogo es grande.</p>
        </div>
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
