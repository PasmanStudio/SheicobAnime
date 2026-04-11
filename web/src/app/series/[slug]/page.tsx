import type { Metadata } from "next";
import { notFound } from "next/navigation";
import Image from "next/image";
import Link from "next/link";
import { getSeriesBySlug, getSeriesEpisodes } from "@/lib/api";

export const dynamic = "force-dynamic";
import { ApiError } from "@/lib/api";
import AdSlot from "@/components/ads/AdSlot";
import Pagination from "@/components/ui/Pagination";

interface Props {
  params: { slug: string };
  searchParams: { page?: string };
}

export async function generateMetadata({ params }: Pick<Props, "params">): Promise<Metadata> {
  try {
    const series = await getSeriesBySlug(params.slug);
    return {
      title: series.title,
      description:
        series.synopsis?.slice(0, 160) ??
        `Watch ${series.title} episodes online.`,
      openGraph: {
        title: series.title,
        description: series.synopsis?.slice(0, 160) ?? "",
        images: series.coverUrl ? [{ url: series.coverUrl }] : [],
      },
    };
  } catch {
    return { title: "Series Not Found" };
  }
}

export default async function SeriesPage({ params, searchParams }: Props) {
  const page = Math.max(1, parseInt(searchParams.page ?? "1", 10));

  let series, episodesPage;
  try {
    [series, episodesPage] = await Promise.all([
      getSeriesBySlug(params.slug),
      getSeriesEpisodes(params.slug, { page, pageSize: 24 }),
    ]);
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) notFound();
    throw err;
  }

  return (
    <div className="container mx-auto px-4 py-8">
      {/* Hero */}
      <div className="flex flex-col md:flex-row gap-6 mb-8">
        {/* Cover */}
        <div className="shrink-0 w-44 md:w-52 self-start">
          <div className="relative aspect-[2/3] rounded-lg overflow-hidden bg-neutral-800 shadow-xl">
            {series.coverUrl ? (
              <Image
                src={series.coverUrl}
                alt={series.title}
                fill
                sizes="(max-width: 768px) 44vw, 208px"
                className="object-cover"
                priority
              />
            ) : (
              <div className="w-full h-full flex items-center justify-center text-neutral-500 text-sm">
                No cover
              </div>
            )}
          </div>
        </div>

        {/* Info */}
        <div className="flex-1 space-y-3">
          <h1 className="text-2xl md:text-3xl font-bold text-white">
            {series.title}
          </h1>
          {series.titleRomaji && series.titleRomaji !== series.title && (
            <p className="text-neutral-400 text-sm">{series.titleRomaji}</p>
          )}

          {/* Meta badges */}
          <div className="flex flex-wrap gap-2 text-xs">
            {series.status && (
              <span className="px-2 py-0.5 rounded bg-neutral-800 text-neutral-300 capitalize">
                {series.status}
              </span>
            )}
            {series.type && (
              <span className="px-2 py-0.5 rounded bg-neutral-800 text-neutral-300 uppercase">
                {series.type}
              </span>
            )}
            {series.year && (
              <span className="px-2 py-0.5 rounded bg-neutral-800 text-neutral-300">
                {series.year}
              </span>
            )}
            {series.score !== null && (
              <span className="px-2 py-0.5 rounded bg-indigo-900/60 text-indigo-300">
                ★ {series.score.toFixed(1)}
              </span>
            )}
          </div>

          {/* Genres */}
          {series.genres.length > 0 && (
            <div className="flex flex-wrap gap-1.5">
              {series.genres.map((g) => (
                <Link
                  key={g.id}
                  href={`/genres/${encodeURIComponent(g.name)}`}
                  className="px-2 py-0.5 text-xs rounded-full border border-indigo-700/50 text-indigo-400 hover:bg-indigo-700/20 transition-colors"
                >
                  {g.name}
                </Link>
              ))}
            </div>
          )}

          {/* Synopsis */}
          {series.synopsis && (
            <p className="text-sm text-neutral-300 leading-relaxed max-w-2xl">
              {series.synopsis}
            </p>
          )}
        </div>
      </div>

      <AdSlot placement="series_top" />

      {/* Episodes */}
      <section className="mt-8">
        <h2 className="text-lg font-semibold text-white mb-4">
          Episodes
          {series.episodeCount !== null && (
            <span className="ml-2 text-sm text-neutral-500 font-normal">
              ({series.episodeCount} total)
            </span>
          )}
        </h2>

        {episodesPage.data.length === 0 ? (
          <p className="text-neutral-500 text-sm">No episodes available yet.</p>
        ) : (
          <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 gap-2">
            {episodesPage.data.map((ep) => (
              <Link
                key={ep.id}
                href={`/episodes/${ep.id}`}
                className="flex items-center justify-center px-3 py-2.5 rounded-md bg-neutral-800 hover:bg-indigo-700 hover:text-white text-neutral-300 text-sm font-medium transition-colors"
              >
                Ep {ep.episodeNumber}
                {ep.title && (
                  <span className="ml-1.5 text-xs text-neutral-500 truncate hidden sm:inline">
                    {ep.title}
                  </span>
                )}
              </Link>
            ))}
          </div>
        )}

        <Pagination
          page={page}
          total={episodesPage.total}
          pageSize={24}
          basePath={`/series/${params.slug}`}
        />
      </section>

      <AdSlot placement="series_bottom" />
    </div>
  );
}
