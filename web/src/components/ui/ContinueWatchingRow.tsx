"use client";

import { getRecentProgress } from "@/lib/api";
import type { RecentProgress } from "@/lib/types";
import Image from "next/image";
import Link from "next/link";
import { useEffect, useState } from "react";

function formatTime(sec: number): string {
  const s = Math.max(0, Math.floor(sec));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const ss = s % 60;
  if (h > 0) return `${h}:${m.toString().padStart(2, "0")}:${ss.toString().padStart(2, "0")}`;
  return `${m}:${ss.toString().padStart(2, "0")}`;
}

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
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-xl font-bold text-white">▶️ Continuar viendo</h2>
      </div>
      <div className="flex gap-3 overflow-x-auto pb-3 scrollbar-thin scrollbar-track-neutral-800 scrollbar-thumb-neutral-600">
        {items.map((p) => {
          const ratio = p.durationSeconds > 0
            ? Math.min(100, Math.round((p.positionSeconds / p.durationSeconds) * 100))
            : 0;
          return (
            <Link
              key={p.episodeId}
              href={`/series/${p.seriesSlug}/${p.episodeNumber}`}
              className="group relative flex-shrink-0 w-56 rounded-lg overflow-hidden"
            >
              <div className="relative aspect-[16/9] bg-neutral-700 overflow-hidden">
                {p.seriesCoverUrl ? (
                  <Image
                    src={p.seriesCoverUrl}
                    alt={p.seriesTitle ?? "Serie"}
                    fill
                    sizes="224px"
                    className="object-cover group-hover:scale-105 transition-transform duration-300"
                  />
                ) : (
                  <div className="w-full h-full bg-neutral-700" />
                )}
                <div className="absolute inset-0 bg-gradient-to-t from-black/90 via-black/40 to-transparent" />
                <span className="absolute top-2 left-2 bg-orange-500 text-white text-[10px] font-bold px-1.5 py-0.5 rounded shadow">
                  EP {p.episodeNumber}
                </span>
                <span className="absolute top-2 right-2 bg-black/70 text-white text-[10px] font-semibold px-1.5 py-0.5 rounded">
                  {formatTime(p.positionSeconds)}
                </span>
                <div className="absolute bottom-0 left-0 right-0 p-2.5">
                  <p className="text-sm font-semibold text-white line-clamp-2 leading-snug drop-shadow-lg">
                    {p.seriesTitle ?? p.seriesSlug}
                  </p>
                </div>
                <div className="absolute bottom-0 left-0 right-0 h-1 bg-black/60">
                  <div className="h-full bg-orange-500" style={{ width: `${ratio}%` }} />
                </div>
              </div>
            </Link>
          );
        })}
      </div>
    </section>
  );
}
