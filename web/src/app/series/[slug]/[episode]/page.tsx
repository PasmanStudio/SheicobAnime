import AdSlot from "@/components/ads/AdSlot";
import NavigationAdTrigger from "@/components/ads/NavigationAdTrigger";
import CommentSection from "@/components/comments/CommentSection";
import EmbeddedPlayerFrame from "@/components/player/EmbeddedPlayerFrame";
import EpisodeSidebar from "@/components/player/EpisodeSidebar";
import { ApiError, getEpisodeBySlug, getSeriesEpisodes } from "@/lib/api";
import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";

export const dynamic = "force-dynamic";

interface Props {
  params: { slug: string; episode: string };
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const episodeNumber = Number(params.episode);
  if (!Number.isInteger(episodeNumber) || episodeNumber < 1) {
    return { title: "Episode Not Found" };
  }

  try {
    const episode = await getEpisodeBySlug(params.slug, episodeNumber);
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
  const episodeNumber = Number(params.episode);
  if (!Number.isInteger(episodeNumber) || episodeNumber < 1) notFound();

  let episode;
  try {
    episode = await getEpisodeBySlug(params.slug, episodeNumber);
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) notFound();
    throw err;
  }

  // Episode sidebar list is non-critical — never let a slow/failed call 500 the page
  let allEpisodes: Awaited<ReturnType<typeof getSeriesEpisodes>>["data"] = [];
  try {
    ({ data: allEpisodes } = await getSeriesEpisodes(params.slug, { pageSize: 500 }));
  } catch {
    allEpisodes = [];
  }

  const episodeTitle = episode.title
    ? `Episodio ${episode.episodeNumber}: ${episode.title}`
    : `Episodio ${episode.episodeNumber}`;

  const siteUrl = process.env.NEXT_PUBLIC_SITE_URL ?? "https://sheicobanime.vercel.app";
  const pageUrl = `${siteUrl}/series/${params.slug}/${episode.episodeNumber}`;

  return (
    <div className="container mx-auto px-4 py-6 space-y-6 max-w-4xl">
      <NavigationAdTrigger />
      {/* Breadcrumb */}
      {episode.series && (
        <nav className="text-sm text-neutral-500 flex items-center gap-2">
          <Link href="/" className="hover:text-white transition-colors">
            Inicio
          </Link>
          <span>/</span>
          <Link
            href={`/series/${episode.series.slug}`}
            className="hover:text-white transition-colors"
          >
            {episode.series.title}
          </Link>
          <span>/</span>
          <span className="text-neutral-300">{episodeTitle}</span>
        </nav>
      )}

      {/* Title */}
      <h1 className="text-xl md:text-2xl font-bold text-white">
        {episodeTitle}
      </h1>

      <AdSlot placement="episode_top" />

      {/* Player — rendered inside a same-origin iframe to isolate the player
          DOM/JS runtime from the host page. See /app/embed/[episodeId]/. */}
      <EmbeddedPlayerFrame episodeId={episode.id} episodeTitle={episodeTitle} />

      <AdSlot placement="episode_mid" />

      {/* Episode sidebar navigator */}
      <EpisodeSidebar
        episodes={allEpisodes}
        currentEpisodeNumber={episodeNumber}
        seriesSlug={params.slug}
        seriesTitle={episode.series?.title ?? "Serie"}
        seriesCoverUrl={episode.series?.coverUrl}
      />

      {/* Episode metadata */}
      <div className="flex flex-wrap gap-4 text-sm text-neutral-400">
        {episode.durationSecs !== null && (
          <span>
            Duración: {Math.floor(episode.durationSecs / 60)}m{" "}
            {episode.durationSecs % 60}s
          </span>
        )}
        {episode.airedAt && (
          <span>Emitido: {new Date(episode.airedAt).toLocaleDateString()}</span>
        )}
      </div>

      {/* Comments */}
      <section>
        <h2 className="text-lg font-semibold text-white mb-3">Comentarios</h2>
        <CommentSection pageId={`${params.slug}-ep${episode.episodeNumber}`} pageUrl={pageUrl} />
      </section>

      <AdSlot placement="episode_bottom" />
    </div>
  );
}
