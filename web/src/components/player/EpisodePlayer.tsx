"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import type { Mirror, ResolvableMirror, ResolvedSource } from "@/lib/types";
import { ApiError, getResolvableSet, reportMirrorFailure, resolveMirror } from "@/lib/api";
import AdSlot from "@/components/ads/AdSlot";
import CustomVideoPlayer from "./CustomVideoPlayer";

type PlayerState =
  | { kind: "preroll"; mirrorIdx: number; remaining: number }
  | { kind: "resolving"; mirrorIdx: number }
  | { kind: "playing"; mirrorIdx: number; source: ResolvedSource }
  | { kind: "fallback_iframe"; mirrorIdx: number }
  | { kind: "error"; message: string };

const PREROLL_SECONDS = 5;

interface EpisodePlayerProps {
  mirrors: Mirror[];
  episodeTitle: string;
  episodeId: string;
  posterUrl?: string;
}

/**
 * Sheicob episode player.
 *
 * Mirror selection workflow:
 *  1. Fetch resolvable-set → mirrors with resolver support are branded "Sheicob"
 *  2. Resolvable mirrors are reordered to the front
 *  3. On select: pre-roll banner → resolving → custom HTML5 player (or fallback iframe)
 */
export default function EpisodePlayer({
  mirrors,
  episodeTitle,
  episodeId,
  posterUrl,
}: EpisodePlayerProps) {
  const activeMirrors = useMemo(
    () =>
      [...mirrors]
        .filter((m) => m.isActive)
        .sort((a, b) => a.priority - b.priority),
    [mirrors]
  );

  const [resolvableSet, setResolvableSet] = useState<Map<string, ResolvableMirror> | null>(null);
  const [reported, setReported] = useState<Set<string>>(new Set());

  // Reorder: Sheicob (resolvable) first, by priority; then iframe-only by priority
  const orderedMirrors = useMemo(() => {
    if (!resolvableSet) return activeMirrors;
    return [...activeMirrors].sort((a, b) => {
      const aRes = resolvableSet.get(a.id)?.resolvable === true;
      const bRes = resolvableSet.get(b.id)?.resolvable === true;
      if (aRes !== bRes) return aRes ? -1 : 1;
      return a.priority - b.priority;
    });
  }, [activeMirrors, resolvableSet]);

  const [state, setState] = useState<PlayerState>(() =>
    activeMirrors.length > 0
      ? { kind: "preroll", mirrorIdx: 0, remaining: PREROLL_SECONDS }
      : { kind: "error", message: "No hay enlaces disponibles para este episodio." }
  );

  // Fetch resolvable set on mount
  useEffect(() => {
    let cancelled = false;
    getResolvableSet(episodeId)
      .then((data) => {
        if (cancelled) return;
        setResolvableSet(new Map(data.map((d) => [d.mirrorId, d])));
      })
      .catch(() => {
        if (!cancelled) setResolvableSet(new Map());
      });
    return () => {
      cancelled = true;
    };
  }, [episodeId]);

  // Pre-roll countdown
  useEffect(() => {
    if (state.kind !== "preroll") return;
    if (state.remaining <= 0) return;
    const t = setTimeout(() => {
      setState((s) =>
        s.kind === "preroll" ? { ...s, remaining: s.remaining - 1 } : s
      );
    }, 1000);
    return () => clearTimeout(t);
  }, [state]);

  const startMirror = useCallback(
    async (idx: number) => {
      const mirror = orderedMirrors[idx];
      if (!mirror) return;
      const isResolvable = resolvableSet?.get(mirror.id)?.resolvable === true;

      if (!isResolvable) {
        setState({ kind: "fallback_iframe", mirrorIdx: idx });
        return;
      }

      setState({ kind: "resolving", mirrorIdx: idx });
      try {
        const source = await resolveMirror(mirror.id);
        setState((s) => {
          if (s.kind !== "resolving" || s.mirrorIdx !== idx) return s;
          return { kind: "playing", mirrorIdx: idx, source };
        });
      } catch (err) {
        const status = err instanceof ApiError ? err.status : 0;
        if (status === 410) {
          setState({ kind: "error", message: "Este enlace está bloqueado." });
        } else {
          setState({ kind: "fallback_iframe", mirrorIdx: idx });
        }
      }
    },
    [orderedMirrors, resolvableSet]
  );

  const handleSkipPreroll = useCallback(() => {
    if (state.kind !== "preroll") return;
    void startMirror(state.mirrorIdx);
  }, [state, startMirror]);

  const handleMirrorSelect = useCallback(
    (idx: number) => {
      void startMirror(idx);
    },
    [startMirror]
  );

  const handleReport = useCallback(async () => {
    let idx = -1;
    if (
      state.kind === "playing" ||
      state.kind === "fallback_iframe" ||
      state.kind === "resolving" ||
      state.kind === "preroll"
    ) {
      idx = state.mirrorIdx;
    }
    if (idx < 0) return;
    const mirror = orderedMirrors[idx];
    if (!mirror || reported.has(mirror.id)) return;
    setReported((prev) => new Set(prev).add(mirror.id));
    try {
      await reportMirrorFailure(mirror.id);
    } catch {
      // fire-and-forget
    }
  }, [state, orderedMirrors, reported]);

  if (orderedMirrors.length === 0) {
    return (
      <div className="aspect-video w-full bg-neutral-900 flex items-center justify-center rounded-lg border border-neutral-800">
        <p className="text-neutral-400 text-sm">
          No hay enlaces disponibles para este episodio.
        </p>
      </div>
    );
  }

  const currentIdx = state.kind === "error" ? 0 : state.mirrorIdx;
  const currentMirror = orderedMirrors[currentIdx];
  const currentReported = currentMirror ? reported.has(currentMirror.id) : false;

  return (
    <div className="space-y-3">
      {/* Player area */}
      <div className="aspect-video w-full bg-black rounded-lg overflow-hidden shadow-2xl relative">
        {state.kind === "preroll" && (
          <div className="absolute inset-0 flex flex-col items-center justify-center bg-neutral-950 z-10">
            <div className="flex flex-col items-center gap-4">
              <p className="text-xs text-neutral-500 uppercase tracking-wide">Publicidad</p>
              <AdSlot placement="episode_above_player" />
            </div>
            <div className="absolute top-3 right-3">
              {state.remaining > 0 ? (
                <span className="px-3 py-1.5 bg-neutral-800 text-neutral-400 rounded text-xs">
                  Saltar en {state.remaining}s
                </span>
              ) : (
                <button
                  onClick={handleSkipPreroll}
                  className="min-w-[44px] min-h-[44px] px-4 py-2 bg-white text-black rounded font-semibold text-sm hover:bg-neutral-200 transition-colors focus:outline-none focus:ring-2 focus:ring-white"
                  aria-label="Saltar publicidad"
                >
                  Saltar ▶
                </button>
              )}
            </div>
          </div>
        )}

        {state.kind === "resolving" && (
          <div className="absolute inset-0 flex flex-col items-center justify-center bg-neutral-950 z-10 gap-3">
            <div className="w-12 h-12 border-4 border-orange-500 border-t-transparent rounded-full animate-spin" />
            <p className="text-sm text-neutral-300">Cargando reproductor Sheicob…</p>
          </div>
        )}

        {state.kind === "playing" && (
          <CustomVideoPlayer
            key={currentMirror?.id}
            source={state.source}
            poster={posterUrl}
            autoPlay
            onError={() => {
              setState({ kind: "fallback_iframe", mirrorIdx: state.mirrorIdx });
            }}
          />
        )}

        {state.kind === "fallback_iframe" && currentMirror && (
          <iframe
            key={currentMirror.id}
            src={currentMirror.embedUrl}
            title={episodeTitle}
            className="w-full h-full"
            allowFullScreen
            allow="fullscreen; autoplay; encrypted-media; picture-in-picture"
            referrerPolicy="no-referrer"
          />
        )}

        {state.kind === "error" && (
          <div className="absolute inset-0 flex items-center justify-center bg-neutral-950 z-10">
            <p className="text-sm text-red-400">{state.message}</p>
          </div>
        )}
      </div>

      {/* Mirror selector — Sheicob mirrors first, branded gold */}
      <div className="space-y-2">
        <div className="grid grid-cols-3 sm:grid-cols-4 md:grid-cols-5 gap-1.5">
          {orderedMirrors.map((m, i) => {
            const isSheicob = resolvableSet?.get(m.id)?.resolvable === true;
            const selected = i === currentIdx;
            return (
              <button
                key={m.id}
                onClick={() => handleMirrorSelect(i)}
                aria-pressed={selected}
                className={`relative px-3 py-2.5 text-sm font-medium rounded transition-colors text-center ${
                  selected
                    ? isSheicob
                      ? "bg-gradient-to-br from-amber-400 to-orange-500 text-black shadow-md"
                      : "bg-orange-500 text-white shadow-md"
                    : isSheicob
                    ? "bg-amber-950/40 text-amber-300 border border-amber-700/50 hover:bg-amber-900/50"
                    : "bg-neutral-800 text-neutral-300 hover:bg-neutral-700 hover:text-white"
                }`}
              >
                {isSheicob ? (
                  <span className="font-bold tracking-wide">Sheicob</span>
                ) : (
                  m.providerName
                )}
                {m.qualityLabel > 0 && (
                  <span
                    className={`block text-[10px] mt-0.5 ${
                      selected
                        ? isSheicob
                          ? "text-black/70"
                          : "text-orange-200"
                        : isSheicob
                        ? "text-amber-400/70"
                        : "text-neutral-500"
                    }`}
                  >
                    {m.qualityLabel}p
                  </span>
                )}
              </button>
            );
          })}
        </div>

        {/* Report button */}
        <div className="flex justify-end">
          <button
            onClick={handleReport}
            disabled={currentReported}
            className="text-xs text-neutral-600 hover:text-red-400 disabled:text-neutral-700 transition-colors"
          >
            {currentReported ? "Reportado ✓" : "Reportar enlace roto"}
          </button>
        </div>
      </div>
    </div>
  );
}
