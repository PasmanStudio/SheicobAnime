"use client";

import { useState, useCallback, useEffect } from "react";
import type { Mirror } from "@/lib/types";
import { reportMirrorFailure } from "@/lib/api";
import AdSlot from "@/components/ads/AdSlot";

type PlayerState = "ad" | "player";
const PREROLL_SECONDS = 5;

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
  const [playerState, setPlayerState] = useState<PlayerState>("ad");
  const [skipTimer, setSkipTimer] = useState(PREROLL_SECONDS);

  // Pre-roll countdown
  useEffect(() => {
    if (playerState !== "ad") return;
    if (skipTimer <= 0) return;
    const timer = setTimeout(() => setSkipTimer((s) => s - 1), 1000);
    return () => clearTimeout(timer);
  }, [playerState, skipTimer]);

  const handleSkipAd = useCallback(() => {
    setPlayerState("player");
  }, []);

  const handleMirrorSelect = useCallback((idx: number) => {
    setSelectedIdx(idx);
    setReported(false);
    // Don't re-show pre-roll on mirror switch
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
      {/* Player area */}
      <div className="aspect-video w-full bg-black rounded-lg overflow-hidden shadow-2xl relative">
        {playerState === "ad" ? (
          /* Pre-roll ad overlay */
          <div className="absolute inset-0 flex flex-col items-center justify-center bg-neutral-950 z-10">
            <div className="flex flex-col items-center gap-4">
              <p className="text-xs text-neutral-500 uppercase tracking-wide">Publicidad</p>
              <AdSlot placement="episode_above_player" />
            </div>
            <div className="absolute top-3 right-3">
              {skipTimer > 0 ? (
                <span className="px-3 py-1.5 bg-neutral-800 text-neutral-400 rounded text-xs">
                  Saltar en {skipTimer}s
                </span>
              ) : (
                <button
                  onClick={handleSkipAd}
                  className="min-w-[44px] min-h-[44px] px-4 py-2 bg-white text-black rounded font-semibold text-sm hover:bg-neutral-200 transition-colors focus:outline-none focus:ring-2 focus:ring-white"
                  aria-label="Saltar publicidad"
                >
                  Saltar ▶
                </button>
              )}
            </div>
          </div>
        ) : (
          /* Mirror iframe */
          <iframe
            key={current.id}
            src={current.embedUrl}
            title={episodeTitle}
            className="w-full h-full"
            allowFullScreen
            allow="fullscreen; autoplay; encrypted-media; picture-in-picture"
            referrerPolicy="no-referrer"
          />
        )}
      </div>

      {/* Mirror selector — JKAnime-style grid */}
      <div className="space-y-2">
        <div className="grid grid-cols-3 sm:grid-cols-4 md:grid-cols-5 gap-1.5">
          {active.map((m, i) => (
            <button
              key={m.id}
              onClick={() => handleMirrorSelect(i)}
              className={`px-3 py-2.5 text-sm font-medium rounded transition-colors text-center ${
                i === selectedIdx
                  ? "bg-orange-500 text-white shadow-md"
                  : "bg-neutral-800 text-neutral-300 hover:bg-neutral-700 hover:text-white"
              }`}
            >
              {m.providerName}
              {m.qualityLabel > 0 && (
                <span className={`block text-[10px] mt-0.5 ${
                  i === selectedIdx ? "text-orange-200" : "text-neutral-500"
                }`}>
                  {m.qualityLabel}p
                </span>
              )}
            </button>
          ))}
        </div>

        {/* Report button */}
        <div className="flex justify-end">
          <button
            onClick={handleReport}
            disabled={reported}
            className="text-xs text-neutral-600 hover:text-red-400 disabled:text-neutral-700 transition-colors"
          >
            {reported ? "Reportado ✓" : "Reportar enlace roto"}
          </button>
        </div>
      </div>
    </div>
  );
}
