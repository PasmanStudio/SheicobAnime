import AdSlot from "@/components/ads/AdSlot";
import NavigationAdTrigger from "@/components/ads/NavigationAdTrigger";
import CommentSection from "@/components/comments/CommentSection";
import DirectEpisodePlayer from "@/components/player/DirectEpisodePlayer";
import EpisodeSidebar from "@/components/player/EpisodeSidebar";
import { ApiError, getEpisodeBySlug, getEpisodeMirrorsBySlug, getSeriesEpisodes } from "@/lib/api";
import type { Metadata } from "next";
import Image from "next/image";
import Link from "next/link";
import { notFound } from "next/navigation";

export const dynamic = "force-dynamic";

interface Props {
  params: Promise<{ slug: string; episode: string }>;
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { slug, episode: episodeStr } = await params;
  const episodeNumber = Number(episodeStr);
  if (!Number.isInteger(episodeNumber) || episodeNumber < 1) {
    return { title: "Episode Not Found" };
  }

  try {
    const episode = await getEpisodeBySlug(slug, episodeNumber);
    const baseTitle = episode.series
      ? `${episode.series.title} — Episodio ${episode.episodeNumber}`
      : `Episodio ${episode.episodeNumber}`;
    const title = episode.title ? `${baseTitle}: ${episode.title}` : baseTitle;
    return {
      title,
      description: `Watch ${title} online.`,
      openGraph: {
        title,
        images: episode.thumbnailUrl ? [{ url: episode.thumbnailUrl }] : [],
      },
    };
  } catch {
    return { title: "Episode Not Found" };
  }
}

export default async function EpisodePage({ params }: Readonly<Props>) {
  const { slug, episode: episodeStr } = await params;
  const episodeNumber = Number(episodeStr);
  if (!Number.isInteger(episodeNumber) || episodeNumber < 1) notFound();

  let episode;
  try {
    episode = await getEpisodeBySlug(slug, episodeNumber);
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) notFound();
    throw err;
  }

  // Non-critical parallel fetches — never let failures 500 the page
  let allEpisodes: Awaited<ReturnType<typeof getSeriesEpisodes>>["data"] = [];
  let mirrors: Awaited<ReturnType<typeof getEpisodeMirrorsBySlug>> = [];
  try {
    [{ data: allEpisodes }, mirrors] = await Promise.all([
      getSeriesEpisodes(slug, { pageSize: 500 }),
      getEpisodeMirrorsBySlug(slug, episodeNumber),
    ]);
  } catch {
    allEpisodes = [];
    mirrors = [];
  }

  const episodeTitle = episode.title
    ? `Episodio ${episode.episodeNumber}: ${episode.title}`
    : `Episodio ${episode.episodeNumber}`;

  const siteUrl = process.env.NEXT_PUBLIC_SITE_URL ?? "https://sheicobanime.vercel.app";
  const pageUrl = `${siteUrl}/series/${slug}/${episode.episodeNumber}`;

  return (
    <div className="container mx-auto px-4 py-6 max-w-6xl">
      <NavigationAdTrigger />

      {/* Breadcrumb */}
      {episode.series && (
        <nav className="text-sm text-neutral-500 flex flex-wrap items-center gap-1 mb-4">
          <Link href="/" className="hover:text-white transition-colors">
            Inicio
          </Link>
          <span>/</span>
          <Link
            href={`/series/${episode.series.slug}`}
            className="hover:text-white transition-colors truncate max-w-[160px] sm:max-w-none"
          >
            {episode.series.title}
          </Link>
          <span>/</span>
          <span className="text-neutral-300">{episodeTitle}</span>
        </nav>
      )}

      <AdSlot placement="episode_top" />

      {/* ── 2-column layout: player area + sidebar ── */}
      <div className="flex flex-col lg:flex-row gap-4 mt-4">
        {/* Left: Player + title + mirrors */}
        <div className="flex-1 min-w-0">
          <DirectEpisodePlayer
            mirrors={mirrors}
            episodeTitle={episodeTitle}
            seriesTitle={episode.series?.title}
          />

          <AdSlot placement="episode_mid" />

          {/* Series info card */}
          {episode.series && (
            <div className="mt-4 flex items-start gap-4 bg-neutral-900/60 rounded-lg p-4 border border-neutral-800">
              {episode.series.coverUrl && (
                <div className="relative w-14 h-20 shrink-0">
                  <Image
                    src={episode.series.coverUrl}
                    alt={episode.series.title}
                    fill
                    sizes="56px"
                    className="object-cover rounded"
                  />
                </div>
              )}
              <div className="min-w-0">
                <Link
                  href={`/series/${episode.series.slug}`}
                  className="text-base font-semibold text-white hover:text-orange-400 transition-colors"
                >
                  {episode.series.title}
                </Link>
                {episode.series && (
                  <p className="text-sm text-neutral-500 mt-0.5">
                    {allEpisodes.length} episodios
                  </p>
                )}
              </div>
            </div>
          )}

          {/* Episode metadata */}
          {(episode.durationSecs !== null || episode.airedAt) && (
            <div className="flex flex-wrap gap-4 text-sm text-neutral-400 mt-4">
              {episode.durationSecs !== null && (
                <span>
                  Duración: {Math.floor(episode.durationSecs / 60)}m{" "}
                  {episode.durationSecs % 60}s
                </span>
              )}
              {episode.airedAt && (
                <span>
                  Emitido: {new Date(episode.airedAt).toLocaleDateString()}
                </span>
              )}
            </div>
          )}

          {/* Prev / Next navigation */}
          <div className="flex items-center justify-between gap-2 mt-4">
            {episodeNumber > 1 ? (
              <Link
                href={`/series/${slug}/${episodeNumber - 1}`}
                className="flex-1 sm:flex-none text-center px-4 py-3 sm:py-2 bg-neutral-800 hover:bg-neutral-700 text-sm text-neutral-300 hover:text-white rounded-lg transition-colors"
              >
                ‹ Anterior
              </Link>
            ) : (
              <span className="flex-1 sm:flex-none" />
            )}
            {allEpisodes.some((ep) => ep.episodeNumber === episodeNumber + 1) && (
              <Link
                href={`/series/${slug}/${episodeNumber + 1}`}
                className="flex-1 sm:flex-none text-center px-4 py-3 sm:py-2 bg-neutral-800 hover:bg-neutral-700 text-sm text-neutral-300 hover:text-white rounded-lg transition-colors"
              >
                Siguiente ›
              </Link>
            )}
          </div>

          {/* Comments */}
          <section className="mt-6">
            <h2 className="text-lg font-semibold text-white mb-3">Comentarios</h2>
            <CommentSection
              pageId={`${slug}-ep${episode.episodeNumber}`}
              pageUrl={pageUrl}
            />
          </section>

          <AdSlot placement="episode_bottom" />
        </div>

        {/* Right: Episode sidebar — fixed width on desktop, full width stacked on mobile */}
        <div className="w-full lg:w-72 xl:w-80 shrink-0">
          <EpisodeSidebar
            episodes={allEpisodes}
            currentEpisodeNumber={episodeNumber}
            seriesSlug={slug}
            seriesTitle={episode.series?.title ?? "Serie"}
            seriesCoverUrl={episode.series?.coverUrl}
          />
        </div>
      </div>
    </div>
  );
}
