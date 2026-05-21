import AdSlot from "@/components/ads/AdSlot";
import AddToListButton from "@/components/lists/AddToListButton";
import AddToTierButton from "@/components/tierlist/AddToTierButton";
import Pagination from "@/components/ui/Pagination";
import WatchlistButton from "@/components/watchlist/WatchlistButton";
import { ApiError, getSeriesBySlug, getSeriesEpisodes } from "@/lib/api";
import type { Metadata } from "next";
import Image from "next/image";
import Link from "next/link";
import { notFound } from "next/navigation";

export const revalidate = 300;

interface Props {
  params: Promise<{ slug: string }>;
  searchParams: Promise<{ page?: string }>;
}

export async function generateMetadata({ params }: Pick<Props, "params">): Promise<Metadata> {
  try {
    const { slug } = await params;
    const series = await getSeriesBySlug(slug);
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
  const { slug } = await params;
  const { page: pageParam } = await searchParams;
  const page = Math.max(1, parseInt(pageParam ?? "1", 10));

  let series, episodesPage;
  try {
    [series, episodesPage] = await Promise.all([
      getSeriesBySlug(slug),
      getSeriesEpisodes(slug, { page, pageSize: 24 }),
    ]);
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) notFound();
    throw err;
  }

  return (
    <div className="container mx-auto px-4 py-8">
      {/* Hero */}
      <div className="flex flex-col md:flex-row gap-6 mb-8 items-center md:items-start">
        {/* Cover */}
        <div className="shrink-0 w-36 sm:w-44 md:w-52 self-center md:self-start mx-auto md:mx-0">
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
        <div className="flex-1 space-y-4 text-center md:text-left">
          <h1 className="text-2xl md:text-3xl font-bold text-white">
            {series.title}
          </h1>
          {/* Alternative titles */}
          {series.titleRomaji && series.titleRomaji !== series.title && (
            <p className="text-neutral-400 text-sm">{series.titleRomaji}</p>
          )}
          {series.titleNative && (
            <p className="text-neutral-500 text-sm">{series.titleNative}</p>
          )}

          {/* Synopsis */}
          {series.synopsis && (
            <p className="text-sm text-neutral-300 leading-relaxed max-w-2xl">
              {series.synopsis}
            </p>
          )}

          {/* Genres */}
          {series.genres.length > 0 && (
            <div className="flex flex-wrap gap-1.5 justify-center md:justify-start">
              {series.genres.map((g) => (
                <Link
                  key={g.id}
                  href={`/genres/${encodeURIComponent(g.name)}`}
                  className="px-2.5 py-1 text-xs rounded-full border border-indigo-700/50 text-indigo-400 hover:bg-indigo-700/20 transition-colors"
                >
                  {g.name}
                </Link>
              ))}
            </div>
          )}

          {/* Action buttons */}
          <div className="flex flex-wrap gap-2 justify-center md:justify-start">
            <WatchlistButton
              seriesSlug={slug}
              seriesTitle={series.title}
              coverUrl={series.coverUrl}
            />
            <AddToListButton
              seriesSlug={slug}
              seriesTitle={series.title}
              coverUrl={series.coverUrl}
            />
            <AddToTierButton
              seriesSlug={slug}
              seriesTitle={series.title}
              coverUrl={series.coverUrl}
            />
          </div>

          {/* Metadata grid */}
          <dl className="grid grid-cols-2 sm:grid-cols-3 gap-x-6 gap-y-2 text-sm mt-2">
            {series.type && (
              <div>
                <dt className="text-neutral-500 text-xs font-medium">Tipo</dt>
                <dd className="text-neutral-200 capitalize">{
                  series.type === "tv" ? "Serie" :
                  series.type === "movie" ? "Película" :
                  series.type.toUpperCase()
                }</dd>
              </div>
            )}
            {series.status && (
              <div>
                <dt className="text-neutral-500 text-xs font-medium">Estado</dt>
                <dd>
                  <span className={`inline-block px-2 py-0.5 text-xs rounded font-medium ${
                    series.status === "ongoing"
                      ? "bg-green-900/50 text-green-400"
                      : series.status === "completed"
                      ? "bg-blue-900/50 text-blue-400"
                      : series.status === "upcoming"
                      ? "bg-yellow-900/50 text-yellow-400"
                      : "bg-neutral-800 text-neutral-300"
                  }`}>
                    {series.status === "ongoing" ? "En emisión" :
                     series.status === "completed" ? "Concluido" :
                     series.status === "upcoming" ? "Próximamente" :
                     series.status === "hiatus" ? "En pausa" :
                     series.status}
                  </span>
                </dd>
              </div>
            )}
            {series.year && (
              <div>
                <dt className="text-neutral-500 text-xs font-medium">Año</dt>
                <dd className="text-neutral-200">{series.year}</dd>
              </div>
            )}
            {series.studio && (
              <div>
                <dt className="text-neutral-500 text-xs font-medium">Estudio</dt>
                <dd className="text-neutral-200">{series.studio}</dd>
              </div>
            )}
            {series.season && (
              <div>
                <dt className="text-neutral-500 text-xs font-medium">Temporada</dt>
                <dd className="text-neutral-200">{series.season}</dd>
              </div>
            )}
            {series.demographics && (
              <div>
                <dt className="text-neutral-500 text-xs font-medium">Demografía</dt>
                <dd className="text-neutral-200">{series.demographics}</dd>
              </div>
            )}
            {series.episodeCount !== null && series.episodeCount > 0 && (
              <div>
                <dt className="text-neutral-500 text-xs font-medium">Episodios</dt>
                <dd className="text-neutral-200">{series.episodeCount}</dd>
              </div>
            )}
            {series.durationMinutes !== null && (
              <div>
                <dt className="text-neutral-500 text-xs font-medium">Duración</dt>
                <dd className="text-neutral-200">{series.durationMinutes} min</dd>
              </div>
            )}
            {series.airedDate && (
              <div>
                <dt className="text-neutral-500 text-xs font-medium">Emitido</dt>
                <dd className="text-neutral-200">{series.airedDate}</dd>
              </div>
            )}
            {series.language && (
              <div>
                <dt className="text-neutral-500 text-xs font-medium">Idioma</dt>
                <dd className="text-neutral-200">{series.language}</dd>
              </div>
            )}
            {series.quality && (
              <div>
                <dt className="text-neutral-500 text-xs font-medium">Calidad</dt>
                <dd className="text-neutral-200">{series.quality}</dd>
              </div>
            )}
            {series.score !== null && (
              <div>
                <dt className="text-neutral-500 text-xs font-medium">Puntuación</dt>
                <dd className="text-indigo-300">★ {series.score.toFixed(1)}</dd>
              </div>
            )}
          </dl>
        </div>
      </div>

      <AdSlot placement="series_top" />

      {/* Episodes */}
      <section className="mt-8">
        <h2 className="text-lg font-semibold text-white mb-4">
          Episodios
          {series.episodeCount !== null && (
            <span className="ml-2 text-sm text-neutral-500 font-normal">
              ({series.episodeCount} total)
            </span>
          )}
        </h2>

        {episodesPage.data.length === 0 ? (
          <p className="text-neutral-500 text-sm">Aún no hay episodios disponibles.</p>
        ) : (
          <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 gap-2">
            {episodesPage.data.map((ep) => (
              <Link
                key={ep.id}
                href={`/series/${slug}/${ep.episodeNumber}`}
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
          basePath={`/series/${slug}`}
        />
      </section>

      <AdSlot placement="series_bottom" />
    </div>
  );
}
