import type { Metadata } from "next";
import { getSeries } from "@/lib/api";

export const dynamic = "force-dynamic";
import SeriesCard from "@/components/ui/SeriesCard";
import Pagination from "@/components/ui/Pagination";
import AdSlot from "@/components/ads/AdSlot";
import Link from "next/link";

interface Props {
  params: Promise<{ name: string }>;
  searchParams: Promise<{ page?: string }>;
}

export async function generateMetadata({ params }: Pick<Props, "params">): Promise<Metadata> {
  const { name: rawName } = await params;
  const name = decodeURIComponent(rawName);
  return {
    title: `Anime de ${name}`,
    description: `Mira series de anime de ${name} online en SheicobAnime.`,
  };
}

export default async function GenrePage({ params, searchParams }: Props) {
  const { name: rawName } = await params;
  const { page: pageParam } = await searchParams;
  const name = decodeURIComponent(rawName);
  const page = Math.max(1, parseInt(pageParam ?? "1", 10));

  const results = await getSeries({ genre: name, page, pageSize: 24 });

  return (
    <div className="mx-auto max-w-container px-4 py-8 space-y-6">
      {/* Breadcrumb */}
      <nav className="flex items-center gap-2 text-[13px]">
        <Link
          href="/genres"
          className="font-semibold text-brand-bright hover:text-[var(--cyan-200)] transition-colors duration-fast"
        >
          Géneros
        </Link>
        <span className="text-ink-3">/</span>
        <span className="text-ink-2">{name}</span>
      </nav>

      <div className="flex flex-col gap-1">
        <span className="sh-label">
          {results.total.toLocaleString("es-AR")} serie{results.total !== 1 ? "s" : ""}
        </span>
        <span className="sh-section-header items-center">
          <span className="sh-cut" />
          <h1 className="sh-display text-[clamp(22px,3vw,28px)]">{name}</h1>
        </span>
      </div>

      {results.data.length === 0 ? (
        <div className="py-8 text-center text-sm">
          <p className="text-ink-2">Todavía no hay series de este género.</p>
          <p className="mt-1 text-ink-3">
            Explorá el{" "}
            <Link href="/directory" className="font-semibold text-brand-bright">
              directorio
            </Link>{" "}
            mientras tanto.
          </p>
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
        basePath={`/genres/${rawName}`}
      />

      <AdSlot placement="genre_bottom" />
    </div>
  );
}
