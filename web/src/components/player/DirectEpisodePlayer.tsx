"use client";

import type { Mirror } from "@/lib/types";
import { useMemo, useState } from "react";

interface Props {
  mirrors: Mirror[];
  episodeTitle: string;
}

/**
 * Simple iframe-based episode player.
 * - SeekStreaming mirrors are branded "Sheicob" (shown first, highlighted gold)
 * - Other servers are collapsed under "Ver otros servidores ▼"
 * - No custom HTML5 player — relies on the embed provider's own player
 */
export default function DirectEpisodePlayer({ mirrors, episodeTitle }: Readonly<Props>) {
  const activeMirrors = useMemo(
    () => [...mirrors].filter((m) => m.isActive).sort((a, b) => a.priority - b.priority),
    [mirrors],
  );

  const [selectedId, setSelectedId] = useState<string | null>(() => {
    const seek = activeMirrors.find((m) => m.providerName === "seekstreaming");
    return seek?.id ?? activeMirrors[0]?.id ?? null;
  });
  const [showOthers, setShowOthers] = useState(false);

  const selected = activeMirrors.find((m) => m.id === selectedId) ?? activeMirrors[0] ?? null;
  const sheicobMirrors = activeMirrors.filter((m) => m.providerName === "seekstreaming");
  const otherMirrors = activeMirrors.filter((m) => m.providerName !== "seekstreaming");

  if (!selected) {
    return (
      <div className="aspect-video w-full bg-neutral-900 flex items-center justify-center rounded-lg border border-neutral-800">
        <p className="text-neutral-400 text-sm">No hay enlaces disponibles para este episodio.</p>
      </div>
    );
  }

  return (
    <div>
      {/* ── Player ── */}
      <div className="aspect-video w-full bg-black rounded-t-lg overflow-hidden shadow-2xl">
        <iframe
          key={selected.id}
          src={selected.embedUrl}
          title={episodeTitle}
          className="w-full h-full border-0 block"
          allowFullScreen
          allow="autoplay; fullscreen; encrypted-media; picture-in-picture"
          referrerPolicy="no-referrer"
        />
      </div>

      {/* ── Title bar ── */}
      <div className="bg-neutral-900 border-x border-neutral-700/50 px-3 sm:px-4 py-2.5 flex items-center gap-2 min-w-0">
        <h2 className="text-xs sm:text-sm font-bold text-white uppercase truncate flex-1 min-w-0">
          <span className="text-orange-400">{episodeTitle}</span>
        </h2>
      </div>

      {/* ── Mirror selector ── */}
      <div className="bg-neutral-900/80 rounded-b-lg border border-t-0 border-neutral-700/50 overflow-hidden">
        {/* Primary row: Sheicob + "ver otros" toggle */}
        <div className="px-3 sm:px-4 py-3 flex flex-wrap items-center gap-2">
          <span className="text-xs text-neutral-500 uppercase tracking-wide shrink-0">Servidor:</span>

          {/* Sheicob (seekstreaming) buttons */}
          {sheicobMirrors.length > 0 ? (
            sheicobMirrors.map((m) => (
              <button
                key={m.id}
                onClick={() => setSelectedId(m.id)}
                aria-pressed={selectedId === m.id}
                className={`px-4 py-1.5 rounded text-sm font-bold transition-colors focus:outline-none focus:ring-2 focus:ring-amber-500 ${
                  selectedId === m.id
                    ? "bg-gradient-to-br from-amber-500 to-orange-600 text-black shadow-lg"
                    : "bg-amber-950/40 text-amber-300 hover:bg-amber-900/60"
                }`}
              >
                Sheicob
                {m.qualityLabel > 0 && (
                  <span className="ml-1 text-xs opacity-75">{m.qualityLabel}p</span>
                )}
              </button>
            ))
          ) : (
            /* No seekstreaming — show first other mirror as primary */
            otherMirrors.slice(0, 1).map((m) => (
              <button
                key={m.id}
                onClick={() => setSelectedId(m.id)}
                aria-pressed={selectedId === m.id}
                className={`px-4 py-1.5 rounded text-sm font-medium capitalize transition-colors ${
                  selectedId === m.id
                    ? "bg-orange-600 text-white"
                    : "bg-neutral-800 text-neutral-300 hover:bg-neutral-700"
                }`}
              >
                {m.providerName}
                {m.qualityLabel > 0 && <span className="ml-1 text-xs opacity-60">{m.qualityLabel}p</span>}
              </button>
            ))
          )}

          {/* Toggle other servers — pushed right on wide screens, new row wraps naturally on narrow */}
          {otherMirrors.length > 0 && (
            <button
              onClick={() => setShowOthers((v) => !v)}
              className="sm:ml-auto px-3 py-1.5 rounded text-xs text-neutral-400 hover:text-white bg-neutral-800 hover:bg-neutral-700 transition-colors focus:outline-none focus:ring-2 focus:ring-neutral-500 whitespace-nowrap"
            >
              Ver otros servidores {showOthers ? "▲" : "▼"}
            </button>
          )}
        </div>

        {/* Other servers (collapsed by default) */}
        {showOthers && otherMirrors.length > 0 && (
          <div className="px-3 sm:px-4 pb-3 pt-2 flex flex-wrap gap-2 border-t border-neutral-700/40">
            {otherMirrors.map((m) => (
              <button
                key={m.id}
                onClick={() => setSelectedId(m.id)}
                aria-pressed={selectedId === m.id}
                className={`px-3 py-1.5 rounded text-sm capitalize transition-colors focus:outline-none focus:ring-2 focus:ring-neutral-500 ${
                  selectedId === m.id
                    ? "bg-orange-600 text-white"
                    : "bg-neutral-800 text-neutral-300 hover:bg-neutral-700 hover:text-white"
                }`}
              >
                {m.providerName}
                {m.qualityLabel > 0 && (
                  <span className="ml-1 text-xs opacity-60">{m.qualityLabel}p</span>
                )}
              </button>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
