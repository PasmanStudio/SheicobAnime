import type { Metadata } from "next";
import { notFound } from "next/navigation";
import Link from "next/link";
import { getEpisode, getEpisodeMirrors, ApiError } from "@/lib/api";

export const dynamic = "force-dynamic";
import EpisodePlayer from "@/components/player/EpisodePlayer";
import AdSlot from "@/components/ads/AdSlot";
import NavigationAdTrigger from "@/components/ads/NavigationAdTrigger";
import CommentSection from "@/components/comments/CommentSection";

interface Props {
  params: { id: string };
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  try {
    const episode = await getEpisode(params.id);
    const baseTitle = episode.series
      ? `${episode.series.title} — Episode ${episode.episodeNumber}`
      : `Episode ${episode.episodeNumber}`;
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

export default async function EpisodePage({ params }: Props) {
  let episode, mirrors;
  try {
    [episode, mirrors] = await Promise.all([
      getEpisode(params.id),
      getEpisodeMirrors(params.id),
    ]);
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) notFound();
    throw err;
  }

  const episodeTitle = episode.title
    ? `Episode ${episode.episodeNumber}: ${episode.title}`
    : `Episode ${episode.episodeNumber}`;

  // Canonical URL for comments
  const pageUrl =
    (process.env.NEXT_PUBLIC_SITE_URL ?? "") + `/episodes/${episode.id}`;

  return (
    <div className="container mx-auto px-4 py-6 space-y-6 max-w-4xl">
      <NavigationAdTrigger />
      {/* Breadcrumb */}
      {episode.series && (
        <nav className="text-sm text-neutral-500 flex items-center gap-2">
          <Link href="/" className="hover:text-white transition-colors">
            Home
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

      {/* Player — client component, never SSR */}
      <EpisodePlayer mirrors={mirrors} episodeTitle={episodeTitle} />

      <AdSlot placement="episode_mid" />

      {/* Episode metadata */}
      <div className="flex flex-wrap gap-4 text-sm text-neutral-400">
        {episode.durationSecs !== null && (
          <span>
            Duration: {Math.floor(episode.durationSecs / 60)}m{" "}
            {episode.durationSecs % 60}s
          </span>
        )}
        {episode.airedAt && (
          <span>Aired: {new Date(episode.airedAt).toLocaleDateString()}</span>
        )}
      </div>

      {/* Comments */}
      <section>
        <h2 className="text-lg font-semibold text-white mb-3">Comments</h2>
        <CommentSection pageId={episode.id} pageUrl={pageUrl} />
      </section>

      <AdSlot placement="episode_bottom" />
    </div>
  );
}
