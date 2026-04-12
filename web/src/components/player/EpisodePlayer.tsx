"use client";

import { useState, useCallback } from "react";
import type { Mirror } from "@/lib/types";
import { reportMirrorFailure } from "@/lib/api";

interface EpisodePlayerProps {
  mirrors: Mirror[];
  episodeTitle: string;
}

export default function EpisodePlayer({
  mirrors,
  episodeTitle,
}: EpisodePlayerProps) {
  const active = mirrors
    .filter((m) => m.isActive)
    .sort((a, b) => a.priority - b.priority);

  const [selectedIdx, setSelectedIdx] = useState(0);
  const [reported, setReported] = useState(false);

  const handleMirrorSelect = useCallback((idx: number) => {
    setSelectedIdx(idx);
    setReported(false);
  }, []);

  const handleReport = useCallback(async () => {
    if (reported) return;
    const current = active[selectedIdx];
    if (!current) return;
    setReported(true);
    try {
      await reportMirrorFailure(current.id);
    } catch {
      // fire-and-forget — ignore errors silently
    }
  }, [reported, active, selectedIdx]);

  if (active.length === 0) {
    return (
      <div className="aspect-video w-full bg-neutral-900 flex items-center justify-center rounded-lg border border-neutral-800">
        <p className="text-neutral-400 text-sm">
          No mirrors available for this episode.
        </p>
      </div>
    );
  }

  const current = active[selectedIdx];

  return (
    <div className="space-y-3">
      {/* Player */}
      <div className="aspect-video w-full bg-black rounded-lg overflow-hidden shadow-2xl">
        <iframe
          key={current.id}
          src={current.embedUrl}
          title={episodeTitle}
          className="w-full h-full"
          allowFullScreen
          allow="fullscreen; autoplay; encrypted-media; picture-in-picture"
          referrerPolicy="no-referrer"
        />
      </div>

      {/* Mirror selector */}
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-xs text-neutral-500 mr-1">Source:</span>
        {active.map((m, i) => (
          <button
            key={m.id}
            onClick={() => handleMirrorSelect(i)}
            className={`px-3 py-1 text-xs rounded border transition-colors ${
              i === selectedIdx
                ? "border-indigo-500 bg-indigo-500/20 text-white"
                : "border-neutral-700 text-neutral-400 hover:border-neutral-500 hover:text-white"
            }`}
          >
            {m.providerName}
            {m.qualityLabel > 0 && (
              <span className="ml-1 text-neutral-500">{m.qualityLabel}p</span>
            )}
          </button>
        ))}

        {/* Report button */}
        <button
          onClick={handleReport}
          disabled={reported}
          className="ml-auto text-xs text-neutral-600 hover:text-red-400 disabled:text-neutral-700 transition-colors"
        >
          {reported ? "Reported ✓" : "Report broken"}
        </button>
      </div>
    </div>
  );
}
