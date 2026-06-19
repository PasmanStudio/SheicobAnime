import type { Episode } from "@/lib/types";

/**
 * Deep-links the user to the episode's IMDb page (where they log in and rate it),
 * falling back to the series page when the episode has no IMDb id yet. Shows the
 * cached IMDb rating as a hook. Renders nothing when there's no IMDb target at all.
 *
 * Note: IMDb has no API to submit votes — the user rates on imdb.com directly.
 */
export default function ImdbRateButton({ episode }: { readonly episode: Episode }) {
  const ttId = episode.imdbId ?? episode.series?.imdbId ?? null;
  if (!ttId) return null;

  const isEpisode = Boolean(episode.imdbId);
  const href = `https://www.imdb.com/title/${ttId}/`;
  const rating = episode.imdbRating;
  const votes = episode.imdbVotes;

  const title =
    rating != null
      ? `${rating.toFixed(1)} en IMDb${votes != null ? ` · ${votes.toLocaleString("es-AR")} votos` : ""} — calificá ${isEpisode ? "este episodio" : "la serie"}`
      : `Calificá ${isEpisode ? "este episodio" : "la serie"} en IMDb`;

  return (
    <a
      href={href}
      target="_blank"
      rel="noopener noreferrer nofollow"
      title={title}
      className="inline-flex h-7 items-center gap-1.5 rounded-badge border border-line-2 bg-abyss-3 px-2.5 text-xs font-semibold text-ink-1 transition-all duration-fast hover:brightness-110 active:scale-[0.97]"
    >
      <span aria-hidden className="text-gold">★</span>
      {rating != null && (
        <>
          <span className="sh-stat">{rating.toFixed(1)}</span>
          <span className="text-ink-3">·</span>
        </>
      )}
      <span className="font-bold">
        Votá en <span className="text-gold">IMDb</span>
      </span>
    </a>
  );
}
