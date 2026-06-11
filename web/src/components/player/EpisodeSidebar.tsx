"use client";

import { getRecentProgress } from "@/lib/api";
import type { Episode, RecentProgress } from "@/lib/types";
import Image from "next/image";
import Link from "next/link";
import { useEffect, useMemo, useState } from "react";

interface EpisodeSidebarProps {
  episodes: Episode[];
  currentEpisodeNumber: number;
  seriesSlug: string;
  seriesTitle: string;
  seriesCoverUrl?: string | null;
}

export default function EpisodeSidebar({
  episodes,
  currentEpisodeNumber,
  seriesSlug,
  seriesTitle,
  seriesCoverUrl,
}: EpisodeSidebarProps) {
  const [isOpen, setIsOpen] = useState(false); // collapsed by default on mobile
  const [filter, setFilter] = useState("");
  const [progressMap, setProgressMap] = useState<Map<string, RecentProgress>>(new Map());

  useEffect(() => {
    let cancelled = false;
    getRecentProgress(100)
      .then((items) => {
        if (cancelled) return;
        const map = new Map<string, RecentProgress>();
        for (const p of items) map.set(p.episodeId, p);
        setProgressMap(map);
      })
      .catch(() => { /* silent — progress is optional */ });
    return () => { cancelled = true; };
  }, []);

  const filtered = useMemo(() => {
    if (!filter.trim()) return episodes;
    const q = filter.trim().toLowerCase();
    return episodes.filter(
      (ep) =>
        String(ep.episodeNumber).includes(q) ||
        ep.title?.toLowerCase().includes(q)
    );
  }, [episodes, filter]);

  return (
    <div className="rounded-card border border-line-1 bg-abyss-2 overflow-hidden">
      {/* Header — collapsible on mobile, always-visible label on desktop */}
      <button
        onClick={() => setIsOpen(!isOpen)}
        className="flex items-center gap-2.5 px-3.5 py-3 w-full lg:cursor-default border-b border-line-1 text-left"
      >
        <span className="text-[13px] font-bold text-ink-1">Episodios</span>
        <span className="sh-stat text-[11px] text-ink-3 ml-auto">
          {currentEpisodeNumber} / {episodes.length}
        </span>
        <svg
          className={`w-4 h-4 shrink-0 text-ink-3 transition-transform lg:hidden ${isOpen ? "rotate-180" : ""}`}
          fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}
        >
          <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
        </svg>
      </button>

      {/* Panel body — always visible on lg+, toggled on mobile */}
      <div className={`${isOpen ? "block" : "hidden"} lg:block`}>
        {/* Filter input */}
        <div className="p-2 border-b border-line-1">
          <input
            type="text"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder="Filtrar episodio…"
            className="w-full px-3 py-1.5 text-sm bg-abyss-1 text-ink-1 rounded-btn border border-line-1 focus:border-brand focus:outline-none focus:shadow-focus placeholder:text-[var(--text-3)] transition-all duration-fast"
          />
        </div>

        {/* Episode list — scrollable */}
        <div className="max-h-60 sm:max-h-80 lg:max-h-[520px] overflow-y-auto overscroll-contain">
          {filtered.length === 0 ? (
            <p className="text-ink-3 text-sm p-3 text-center">
              No se encontraron episodios.
            </p>
          ) : (
            <ul className="divide-y divide-[var(--border-1)]">
              {filtered.map((ep) => {
                const isCurrent = ep.episodeNumber === currentEpisodeNumber;
                const thumb = ep.thumbnailUrl ?? ep.series?.coverUrl ?? seriesCoverUrl ?? null;
                const prog = progressMap.get(ep.id);
                const ratio = prog && prog.durationSeconds > 0
                  ? Math.min(100, Math.round((prog.positionSeconds / prog.durationSeconds) * 100))
                  : 0;
                const watched = Boolean(prog?.completed);

                return (
                  <li key={ep.id}>
                    <Link
                      href={`/series/${seriesSlug}/${ep.episodeNumber}`}
                      onClick={() => setIsOpen(false)}
                      className={`flex items-center gap-3 px-3 py-2 transition-colors duration-fast border-l-[3px] ${
                        isCurrent
                          ? "bg-[var(--accent-muted)] border-brand"
                          : "hover:bg-abyss-3 border-transparent"
                      }`}
                    >
                      {/* Thumbnail */}
                      <div className="relative w-14 aspect-video rounded-badge overflow-hidden bg-abyss-3 shrink-0">
                        {thumb ? (
                          <Image
                            src={thumb}
                            alt={`Ep ${ep.episodeNumber}`}
                            fill
                            sizes="56px"
                            className="object-cover"
                          />
                        ) : (
                          <div className="w-full h-full flex items-center justify-center font-display italic font-black text-ink-3 text-xs">
                            EP
                          </div>
                        )}
                        {prog && !prog.completed && ratio > 0 && (
                          <div className="absolute bottom-0 left-0 right-0 h-1 bg-[rgba(5,7,11,0.6)]">
                            <div
                              className="h-full"
                              style={{ width: `${ratio}%`, background: "var(--grad-action)" }}
                            />
                          </div>
                        )}
                      </div>

                      {/* Info */}
                      <div className="flex-1 min-w-0">
                        <p
                          className={`sh-stat text-xs truncate ${
                            isCurrent
                              ? "text-brand-bright"
                              : watched
                                ? "text-ink-3"
                                : "text-ink-2"
                          }`}
                        >
                          EP {String(ep.episodeNumber).padStart(2, "0")}
                          {isCurrent && (
                            <span className="ml-2 font-sans font-semibold text-ink-1 normal-case">
                              Reproduciendo
                            </span>
                          )}
                          {!isCurrent && watched && (
                            <span className="ml-2 font-sans font-medium text-ink-3 normal-case">
                              Visto
                            </span>
                          )}
                        </p>
                        {/* Episode-specific subtitle: real episode title (not series name) or aired date */}
                        {ep.title && ep.title !== seriesTitle ? (
                          <p className={`text-[11px] truncate ${watched && !isCurrent ? "text-ink-3" : "text-ink-2"}`}>
                            {ep.title}
                          </p>
                        ) : ep.airedAt ? (
                          <p className="sh-stat text-[10px] text-ink-3">
                            {new Date(ep.airedAt).toLocaleDateString("es-AR")}
                          </p>
                        ) : null}
                      </div>
                    </Link>
                  </li>
                );
              })}
            </ul>
          )}
        </div>

        {/* Footer — view all link */}
        <div className="border-t border-line-1 p-2">
          <Link
            href={`/series/${seriesSlug}`}
            className="block text-center text-xs font-semibold text-ink-3 hover:text-brand-bright transition-colors duration-fast py-1"
          >
            Ver todos los episodios →
          </Link>
        </div>
      </div>
    </div>
  );
}
