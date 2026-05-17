import { redirect, notFound } from "next/navigation";
import { getEpisode, ApiError } from "@/lib/api";

export const revalidate = 300;

interface Props {
  params: Promise<{ id: string }>;
}

/**
 * Legacy route — redirects /episodes/{guid} to /series/{slug}/{episodeNumber}.
 * Keeps bookmarks and search engine links alive.
 */
export default async function EpisodeRedirectPage({ params }: Props) {
  const { id } = await params;
  let episode;
  try {
    episode = await getEpisode(id);
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) notFound();
    throw err;
  }

  if (episode.series) {
    redirect(`/series/${episode.series.slug}/${episode.episodeNumber}`);
  }

  // Fallback: series data missing — redirect to home
  redirect("/");
}
