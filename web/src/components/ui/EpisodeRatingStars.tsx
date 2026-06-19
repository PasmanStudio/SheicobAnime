"use client";

import { getEpisodeRating, submitEpisodeRating } from "@/lib/api";
import type { EpisodeRatingStats } from "@/lib/types";
import { useEffect, useState } from "react";

/**
 * Native 1–5 star rating for an episode — this is the vote that actually counts on our side
 * (keyed by device id, works logged in or not). One tap, instant, no leaving the page.
 * The IMDb button next to it is just social proof + an optional deep-link.
 */
export default function EpisodeRatingStars({ episodeId }: { readonly episodeId: string }) {
  const [stats, setStats] = useState<EpisodeRatingStats | null>(null);
  const [hover, setHover] = useState(0);
  const [pending, setPending] = useState(false);

  useEffect(() => {
    let alive = true;
    getEpisodeRating(episodeId)
      .then((s) => alive && setStats(s))
      .catch(() => {});
    return () => {
      alive = false;
    };
  }, [episodeId]);

  const myRating = stats?.myRating ?? 0;
  const shown = hover || myRating;

  async function rate(value: number) {
    if (pending) return;
    setPending(true);
    setStats((prev) => (prev ? { ...prev, myRating: value } : prev)); // optimistic
    try {
      setStats(await submitEpisodeRating(episodeId, value));
    } catch {
      // best-effort — leave optimistic value; next visit re-syncs
    } finally {
      setPending(false);
    }
  }

  return (
    <div className="flex items-center gap-2.5 flex-wrap">
      <div
        className="flex items-center gap-0.5"
        role="radiogroup"
        aria-label="Calificá este episodio"
        onMouseLeave={() => setHover(0)}
      >
        {[1, 2, 3, 4, 5].map((v) => (
          <button
            key={v}
            type="button"
            role="radio"
            aria-checked={myRating === v}
            aria-label={`${v} ${v === 1 ? "estrella" : "estrellas"}`}
            disabled={pending}
            onMouseEnter={() => setHover(v)}
            onFocus={() => setHover(v)}
            onClick={() => rate(v)}
            className={`p-0.5 text-xl leading-none transition-transform duration-fast hover:scale-110 active:scale-95 ${
              v <= shown ? "text-gold" : "text-ink-3"
            }`}
          >
            {v <= shown ? "★" : "☆"}
          </button>
        ))}
      </div>

      <span className="sh-stat text-xs text-ink-3">
        {stats && stats.count > 0 ? (
          <>
            {stats.average.toFixed(1)} <span className="text-ink-3">·</span> {stats.count}{" "}
            {stats.count === 1 ? "voto" : "votos"}
            {myRating > 0 && <span className="text-ink-2"> · tu nota {myRating}</span>}
          </>
        ) : myRating > 0 ? (
          <span className="text-ink-2">tu nota {myRating}</span>
        ) : (
          "Sé el primero en calificar"
        )}
      </span>
    </div>
  );
}
