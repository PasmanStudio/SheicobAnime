import EmbedHeightReporter from "@/components/player/EmbedHeightReporter";
import EpisodePlayer from "@/components/player/EpisodePlayer";
import { ApiError, getEpisode, getEpisodeMirrors } from "@/lib/api";
import { notFound } from "next/navigation";

export const dynamic = "force-dynamic";

interface Props {
  params: { episodeId: string };
}

/**
 * Isolated player frame. Rendered inside an &lt;iframe&gt; from
 * /series/[slug]/[episode] to shield the player's DOM + JS runtime from the
 * host page (harder for naïve scrapers to reach the &lt;video&gt; element or
 * tap into resolved source state).
 *
 * Served with X-Frame-Options: SAMEORIGIN so the iframe only works on our own
 * origin; any third party trying to embed it is denied.
 */
export default async function EmbedPlayerPage({ params }: Readonly<Props>) {
  let episode, mirrors;
  try {
    [episode, mirrors] = await Promise.all([
      getEpisode(params.episodeId),
      getEpisodeMirrors(params.episodeId),
    ]);
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) notFound();
    throw err;
  }

  const episodeTitle = episode.title
    ? `Episodio ${episode.episodeNumber}: ${episode.title}`
    : `Episodio ${episode.episodeNumber}`;

  return (
    <div className="w-full h-full">
      <EpisodePlayer
        mirrors={mirrors}
        episodeTitle={episodeTitle}
        episodeId={episode.id}
        posterUrl={episode.thumbnailUrl ?? undefined}
      />
      <EmbedHeightReporter />
    </div>
  );
}
