"use client";

import { useState, useMemo } from "react";
import Image from "next/image";
import Link from "next/link";
import type { Episode } from "@/lib/types";

interface EpisodeSidebarProps {
  episodes: Episode[];
  currentEpisodeNumber: number;
  seriesSlug: string;
  seriesTitle: string;
}

const PAGE_SIZE = 50;

export default function EpisodeSidebar({
  episodes,
  currentEpisodeNumber,
  seriesSlug,
  seriesTitle,
}: EpisodeSidebarProps) {
  const [isOpen, setIsOpen] = useState(false);
  const [filter, setFilter] = useState("");

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
    <div className="relative">
      {/* Toggle button */}
      <button
        onClick={() => setIsOpen(!isOpen)}
        className="flex items-center gap-2 px-3 py-2 rounded-lg bg-neutral-800 hover:bg-neutral-700 text-neutral-300 text-sm font-medium transition-colors w-full"
      >
        <svg className="w-4 h-4 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M4 6h16M4 12h16M4 18h16" />
        </svg>
        <span>Ep {currentEpisodeNumber}</span>
        <span className="text-neutral-500 text-xs ml-auto">
          {episodes.length} episodios
        </span>
        <svg
          className={`w-4 h-4 shrink-0 transition-transform ${isOpen ? "rotate-180" : ""}`}
          fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}
        >
          <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
        </svg>
      </button>

      {/* Dropdown panel */}
      {isOpen && (
        <div className="mt-2 rounded-lg border border-neutral-700 bg-neutral-900 shadow-xl overflow-hidden">
          {/* Filter input */}
          <div className="p-2 border-b border-neutral-800">
            <input
              type="text"
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
              placeholder="Filtrar episodio..."
              className="w-full px-3 py-1.5 text-sm bg-neutral-800 text-white rounded border border-neutral-700 focus:border-indigo-500 focus:outline-none placeholder:text-neutral-500"
            />
          </div>

          {/* Episode list */}
          <div className="max-h-80 overflow-y-auto">
            {filtered.length === 0 ? (
              <p className="text-neutral-500 text-sm p-3 text-center">
                No se encontraron episodios.
              </p>
            ) : (
              <ul className="divide-y divide-neutral-800">
                {filtered.map((ep) => {
                  const isCurrent = ep.episodeNumber === currentEpisodeNumber;
                  const thumb = ep.thumbnailUrl ?? ep.series?.coverUrl ?? null;

                  return (
                    <li key={ep.id}>
                      <Link
                        href={`/series/${seriesSlug}/${ep.episodeNumber}`}
                        onClick={() => setIsOpen(false)}
                        className={`flex items-center gap-3 px-3 py-2.5 transition-colors ${
                          isCurrent
                            ? "bg-indigo-600/20 border-l-2 border-indigo-500"
                            : "hover:bg-neutral-800 border-l-2 border-transparent"
                        }`}
                      >
                        {/* Thumbnail */}
                        <div className="relative w-16 aspect-video rounded overflow-hidden bg-neutral-800 shrink-0">
                          {thumb ? (
                            <Image
                              src={thumb}
                              alt={`Ep ${ep.episodeNumber}`}
                              fill
                              sizes="64px"
                              className="object-cover"
                            />
                          ) : (
                            <div className="w-full h-full flex items-center justify-center text-neutral-600 text-[10px]">
                              Sin img
                            </div>
                          )}
                        </div>

                        {/* Info */}
                        <div className="flex-1 min-w-0">
                          <p className={`text-sm font-medium truncate ${
                            isCurrent ? "text-indigo-300" : "text-neutral-200"
                          }`}>
                            Episodio {ep.episodeNumber}
                            {ep.title && (
                              <span className="text-neutral-500 font-normal">
                                {" "}— {ep.title}
                              </span>
                            )}
                          </p>
                          <p className="text-[11px] text-neutral-500 truncate">
                            {seriesTitle}
                          </p>
                          {ep.airedAt && (
                            <p className="text-[11px] text-neutral-600">
                              {new Date(ep.airedAt).toLocaleDateString()}
                            </p>
                          )}
                        </div>
                      </Link>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>

          {/* Footer — view all link */}
          <div className="border-t border-neutral-800 p-2">
            <Link
              href={`/series/${seriesSlug}`}
              className="block text-center text-xs text-neutral-400 hover:text-white transition-colors py-1"
            >
              ≡ Ver todos los episodios
            </Link>
          </div>
        </div>
      )}
    </div>
  );
}
