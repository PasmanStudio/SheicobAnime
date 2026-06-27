import { redirect, notFound } from "next/navigation";
import { getEpisode, ApiError } from "@/lib/api";

// Respaldo de fondo (la frescura la da la purga on-demand del scraper). Alto
// porque hay MUCHAS variantes de esta ruta y cada regeneración por TTL es un
// write a KV — el free tier son 1000/día. Antes 300s.
export const revalidate = 21600;

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
