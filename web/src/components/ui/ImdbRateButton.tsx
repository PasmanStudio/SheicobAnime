import type { Episode } from "@/lib/types";

/**
 * Sends the user to IMDb to rate the episode. Two modes:
 *   - Exact deep-link to the episode/series title page when we already resolved an
 *     imdb_id (via the optional TMDB-backed resolver — not required for this to work).
 *   - Otherwise, an IMDb search query for "{series title} episode {n}" — no API key,
 *     no TMDB dependency, always available. Good enough to land the user on (or very
 *     near) the right title and get them rating.
 *
 * Note: IMDb has no API to submit votes — the user always rates on imdb.com directly.
 */
export default function ImdbRateButton({ episode }: { readonly episode: Episode }) {
  const seriesTitle = episode.series?.title;
  const ttId = episode.imdbId ?? episode.series?.imdbId ?? null;
  if (!ttId && !seriesTitle) return null; // nothing to search or link to

  const isEpisode = Boolean(episode.imdbId);
  const href = ttId
    ? `https://www.imdb.com/title/${ttId}/`
    : `https://www.imdb.com/find/?q=${encodeURIComponent(`${seriesTitle} episode ${episode.episodeNumber}`)}&s=tt`;
  const rating = episode.imdbRating;
  const votes = episode.imdbVotes;

  const title = ttId
    ? rating != null
      ? `${rating.toFixed(1)} en IMDb${votes != null ? ` · ${votes.toLocaleString("es-AR")} votos` : ""} — calificá ${isEpisode ? "este episodio" : "la serie"}`
      : `Calificá ${isEpisode ? "este episodio" : "la serie"} en IMDb`
    : `Buscá este episodio en IMDb y calificalo`;

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
