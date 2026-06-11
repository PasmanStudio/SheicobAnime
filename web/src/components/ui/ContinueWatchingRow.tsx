"use client";

import EpisodeCard from "@/components/ui/EpisodeCard";
import SectionHeader from "@/components/ui/SectionHeader";
import { getRecentProgress } from "@/lib/api";
import type { RecentProgress } from "@/lib/types";
import { useEffect, useState } from "react";

export default function ContinueWatchingRow() {
  const [items, setItems] = useState<RecentProgress[]>([]);
  const [loaded, setLoaded] = useState(false);

  useEffect(() => {
    let cancelled = false;
    getRecentProgress(20)
      .then((data) => {
        if (!cancelled) {
          setItems(data.filter((p) => !p.completed && p.positionSeconds >= 10));
          setLoaded(true);
        }
      })
      .catch(() => {
        if (!cancelled) setLoaded(true);
      });
    return () => { cancelled = true; };
  }, []);

  if (!loaded || items.length === 0) return null;

  return (
    <section>
      <SectionHeader title="Continuar viendo" className="mb-4" />
      <div className="sh-scroll-row pb-1">
        {items.map((p) => {
          const progress =
            p.durationSeconds > 0
              ? Math.min(1, p.positionSeconds / p.durationSeconds)
              : 0;
          return (
            <EpisodeCard
              key={p.episodeId}
              href={`/series/${p.seriesSlug}/${p.episodeNumber}`}
              seriesTitle={p.seriesTitle ?? p.seriesSlug}
              episodeNumber={p.episodeNumber}
              thumbnailUrl={p.seriesCoverUrl}
              progress={progress}
              className="w-60 shrink-0"
            />
          );
        })}
      </div>
    </section>
  );
}
