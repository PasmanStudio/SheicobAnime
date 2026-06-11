import type { Metadata } from "next";
import { searchSeries } from "@/lib/api";

export const dynamic = "force-dynamic";
import SeriesCard from "@/components/ui/SeriesCard";
import Pagination from "@/components/ui/Pagination";
import AdSlot from "@/components/ads/AdSlot";

interface Props {
  searchParams: Promise<{ q?: string; page?: string }>;
}

export async function generateMetadata({ searchParams }: Props): Promise<Metadata> {
  const { q: rawQ } = await searchParams;
  const q = rawQ?.trim() ?? "";
  return {
    title: q ? `Buscar: ${q}` : "Buscar anime",
    description: q
      ? `Resultados de búsqueda para "${q}" en SheicobAnime.`
      : "Buscá series de anime en SheicobAnime.",
  };
}

export default async function SearchPage({ searchParams }: Props) {
  const sp = await searchParams;
  const q = sp.q?.trim() ?? "";
  const page = Math.max(1, parseInt(sp.page ?? "1", 10));

  if (!q) {
    return (
      <div className="mx-auto max-w-container px-4 py-16 text-center space-y-2">
        <h1 className="sh-display text-2xl">Buscar anime</h1>
        <p className="text-sm text-ink-2">Todavía no buscaste nada.</p>
        <p className="text-sm text-ink-3">Usá la lupa de arriba para encontrar tu próxima serie.</p>
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
    <div className="mx-auto max-w-container px-4 py-8 space-y-6">
      <div className="flex flex-col gap-1">
        <span className="sh-label">
          {results.total.toLocaleString("es-AR")} resultado{results.total !== 1 ? "s" : ""}
        </span>
        <span className="sh-section-header items-center">
          <span className="sh-cut" />
          <h1 className="sh-display text-[clamp(20px,2.6vw,26px)]">
            &ldquo;{q}&rdquo;
          </h1>
        </span>
      </div>

      {results.data.length === 0 ? (
        <div className="py-8 text-center text-sm">
          <p className="text-ink-2">No encontramos nada con &ldquo;{q}&rdquo;.</p>
          <p className="mt-1 text-ink-3">Probá con otro término o con el título en romaji.</p>
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
        pageSize={24}
        basePath={`/search?q=${encodeURIComponent(q)}`}
      />

      <AdSlot placement="search_bottom" />
    </div>
  );
}
