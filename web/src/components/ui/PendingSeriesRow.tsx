"use client";

import EpisodeCard from "@/components/ui/EpisodeCard";
import SectionHeader from "@/components/ui/SectionHeader";
import { getPendingSeries } from "@/lib/api";
import type { PendingSeries } from "@/lib/types";
import { useEffect, useState } from "react";

/**
 * "Seguir mirando" — series donde terminaste un episodio y el siguiente ya
 * está disponible. Funciona logueado o no (el progreso va por device cookie).
 * Se oculta sola si no hay nada pendiente.
 */
export default function PendingSeriesRow() {
  const [items, setItems] = useState<PendingSeries[]>([]);
  const [loaded, setLoaded] = useState(false);

  useEffect(() => {
    let cancelled = false;
    getPendingSeries(12)
      .then((data) => {
        if (!cancelled) {
          setItems(data);
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
      <SectionHeader
        title="Seguir mirando"
        eyebrow="Te quedaron episodios pendientes"
        className="mb-4"
      />
      <div className="sh-scroll-row pb-1">
        {items.map((p) => (
          <EpisodeCard
            key={p.seriesSlug}
            href={`/series/${p.seriesSlug}/${p.nextEpisodeNumber}`}
            seriesTitle={p.seriesTitle ?? p.seriesSlug}
            episodeNumber={p.nextEpisodeNumber}
            title={`Viste hasta el EP ${p.lastWatchedEpisode}`}
            thumbnailUrl={p.seriesCoverUrl}
            className="w-60 shrink-0"
          />
        ))}
      </div>
    </section>
  );
}
