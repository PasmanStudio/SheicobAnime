"use client";

import AdSlot from "@/components/ads/AdSlot";
import { useWatchProgress } from "@/hooks/useWatchProgress";
import { ApiError, getResolvableSet, reportMirrorFailure, resolveMirror } from "@/lib/api";
import type { Mirror, ResolvableMirror, ResolvedSource } from "@/lib/types";
import { useCallback, useEffect, useMemo, useState } from "react";
import CustomVideoPlayer from "./CustomVideoPlayer";
import ResumePrompt from "./ResumePrompt";
import VastPreroll from "./VastPreroll";

// ExoClick VAST preroll — zone 5904192 (In-Stream Video, site SheicobAnime).
// Zone ID is public (non-secret); env vars allow override without a redeploy.
// The VAST XML is fetched through our API proxy (/vast?url=...) because ExoClick
// does NOT send Access-Control-Allow-Origin headers, so a direct browser fetch()
// is silently blocked by CORS.  The proxy also follows VAST Wrapper chains
// server-side so the client always receives a resolved InLine VAST.
const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000";
const EXOCLICK_ZONE = process.env.NEXT_PUBLIC_EXOCLICK_VAST_ZONE_ID ?? "5904192";
const RAW_VAST_URL =
  process.env.NEXT_PUBLIC_EXOCLICK_VAST_URL ||
  `https://s.magsrv.com/v1/vast.php?idzone=${EXOCLICK_ZONE}`;
const VAST_URL = `${API_URL}/vast?url=${encodeURIComponent(RAW_VAST_URL)}`;

// A "display entry" is what the user sees as a button:
//   - "sheicob" = the group of ALL resolvable mirrors (internally auto-failovers)
//   - "iframe"  = a single non-resolvable mirror, shown with its provider name
type DisplayEntry =
  | { kind: "sheicob" }
  | { kind: "iframe"; mirror: Mirror };

type PlayerState =
  | { kind: "preroll"; displayIdx: number; sheicobTry: number; remaining: number }
  | { kind: "resolving"; displayIdx: number; sheicobTry: number }
  | { kind: "playing"; displayIdx: number; sheicobTry: number; source: ResolvedSource }
  | { kind: "fallback_iframe"; displayIdx: number }
  | { kind: "error"; message: string };

const PREROLL_SECONDS = 5;

interface EpisodePlayerProps {
  mirrors: Mirror[];
  episodeTitle: string;
  seriesTitle?: string;
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
  seriesTitle: _seriesTitle,
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
  // When a SeekStreaming mirror exists, collapse external servers by default.
  const [showAllServers, setShowAllServers] = useState(false);

  // Watch progress + resume prompt
  const { initial: progress, canResume, reportProgress, flush } = useWatchProgress(episodeId);
  const [resumeDecision, setResumeDecision] = useState<"pending" | "accepted" | "dismissed">("pending");
  const resumeFrom =
    resumeDecision === "accepted" && progress ? progress.positionSeconds : 0;

  // Resolvable mirrors, sorted by priority — the "Sheicob" group auto-failovers through this list
  const resolvableMirrors = useMemo(() => {
    if (!resolvableSet) return [] as Mirror[];
    return activeMirrors.filter(
      (m) => resolvableSet.get(m.id)?.resolvable === true
    );
  }, [activeMirrors, resolvableSet]);

  // Non-resolvable mirrors, shown individually with their provider name
  const iframeMirrors = useMemo(() => {
    if (!resolvableSet) return activeMirrors; // before resolvableSet loads, treat all as iframe
    return activeMirrors.filter(
      (m) => resolvableSet.get(m.id)?.resolvable !== true
    );
  }, [activeMirrors, resolvableSet]);

  // Display entries: one "Sheicob" group button (if any resolvable) + every iframe mirror
  const displayEntries = useMemo<DisplayEntry[]>(() => {
    const entries: DisplayEntry[] = [];
    if (resolvableMirrors.length > 0) entries.push({ kind: "sheicob" });
    for (const m of iframeMirrors) entries.push({ kind: "iframe", mirror: m });
    return entries;
  }, [resolvableMirrors, iframeMirrors]);

  const [state, setState] = useState<PlayerState>(() =>
    activeMirrors.length > 0
      ? { kind: "preroll", displayIdx: 0, sheicobTry: 0, remaining: PREROLL_SECONDS }
      : { kind: "error", message: "No hay enlaces disponibles para este episodio." }
  );

  // When VAST returns no fill, show the fallback Adsterra banner instead of blank
  const [vastNoFill, setVastNoFill] = useState(false);
  const handleVastNoFill = useCallback(() => setVastNoFill(true), []);

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

  // Resolve the (displayIdx, sheicobTry) pair → play / fallback / advance
  const startEntry = useCallback(
    async (displayIdx: number, sheicobTry: number) => {
      const entry = displayEntries[displayIdx];
      if (!entry) return;

      if (entry.kind === "iframe") {
        setState({ kind: "fallback_iframe", displayIdx });
        return;
      }

      // Sheicob group — try resolvableMirrors[sheicobTry]
      const mirror = resolvableMirrors[sheicobTry];
      if (!mirror) {
        setState({
          kind: "error",
          message:
            "Ningún enlace Sheicob está disponible en este momento. Probá otro servidor.",
        });
        return;
      }

      setState({ kind: "resolving", displayIdx, sheicobTry });
      try {
        const source = await resolveMirror(mirror.id);
        setState((s) => {
          if (
            s.kind !== "resolving" ||
            s.displayIdx !== displayIdx ||
            s.sheicobTry !== sheicobTry
          ) {
            return s;
          }
          return { kind: "playing", displayIdx, sheicobTry, source };
        });
      } catch (err) {
        const status = err instanceof ApiError ? err.status : 0;
        if (status === 410) {
          setState({ kind: "error", message: "Este enlace está bloqueado." });
          return;
        }
        // Auto-failover to the next resolvable mirror in the group
        if (sheicobTry + 1 < resolvableMirrors.length) {
          void startEntry(displayIdx, sheicobTry + 1);
        } else {
          setState({
            kind: "error",
            message:
              "No pudimos cargar ningún enlace Sheicob. Probá otro servidor.",
          });
        }
      }
    },
    [displayEntries, resolvableMirrors]
  );

  const handleSkipPreroll = useCallback(() => {
    if (state.kind !== "preroll") return;
    const entry = displayEntries[state.displayIdx];
    const isSheicob = entry?.kind === "sheicob";
    // Resume only matters for resolvable mirrors (iframes can't seek).
    // Hold in "resolving" (spinner + prompt overlay) until the viewer picks Aceptar/Cancelar.
    if (isSheicob && canResume && resumeDecision === "pending") {
      setState({
        kind: "resolving",
        displayIdx: state.displayIdx,
        sheicobTry: state.sheicobTry,
      });
      return;
    }
    void startEntry(state.displayIdx, state.sheicobTry);
  }, [state, startEntry, canResume, resumeDecision, displayEntries]);

  // Once the viewer decides (accept/dismiss), start playback.
  useEffect(() => {
    if (resumeDecision === "pending") return;
    if (state.kind !== "resolving") return;
    void startEntry(state.displayIdx, state.sheicobTry);
    // Only trigger on decision change, not on every state transition
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [resumeDecision]);

  // Flush progress before switching entry (so the old mirror's position is saved)
  const handleEntrySelect = useCallback(
    (displayIdx: number) => {
      flush();
      void startEntry(displayIdx, 0);
    },
    [startEntry, flush]
  );

  // Currently playing underlying mirror (if any) — used for reporting broken links
  const activeUnderlyingMirror = useMemo<Mirror | null>(() => {
    if (state.kind === "fallback_iframe") {
      const entry = displayEntries[state.displayIdx];
      return entry?.kind === "iframe" ? entry.mirror : null;
    }
    if (
      state.kind === "playing" ||
      state.kind === "resolving" ||
      state.kind === "preroll"
    ) {
      const entry = displayEntries[state.displayIdx];
      if (entry?.kind === "sheicob") {
        return resolvableMirrors[state.sheicobTry] ?? null;
      }
      if (entry?.kind === "iframe") return entry.mirror;
    }
    return null;
  }, [state, displayEntries, resolvableMirrors]);

  const handleReport = useCallback(async () => {
    const mirror = activeUnderlyingMirror;
    if (!mirror || reported.has(mirror.id)) return;
    setReported((prev) => new Set(prev).add(mirror.id));
    try {
      await reportMirrorFailure(mirror.id);
    } catch {
      // fire-and-forget
    }
  }, [activeUnderlyingMirror, reported]);

  if (displayEntries.length === 0) {
    return (
      <div className="aspect-video w-full bg-neutral-900 flex items-center justify-center rounded-lg border border-neutral-800">
        <p className="text-neutral-400 text-sm">
          No hay enlaces disponibles para este episodio.
        </p>
      </div>
    );
  }

  const currentDisplayIdx = state.kind === "error" ? -1 : state.displayIdx;
  const currentReported = activeUnderlyingMirror
    ? reported.has(activeUnderlyingMirror.id)
    : false;

  return (
    <div>
      {/* ── Player area (rounded top only) ── */}
      <div className="aspect-video w-full bg-black rounded-t-lg overflow-hidden shadow-2xl relative">
        {state.kind === "preroll" && VAST_URL && !vastNoFill && (
          <VastPreroll vastUrl={VAST_URL} onComplete={handleSkipPreroll} onNoFill={handleVastNoFill} />
        )}

        {state.kind === "preroll" && (!VAST_URL || vastNoFill) && (
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
            key={`${activeUnderlyingMirror?.id ?? "src"}-${state.sheicobTry}`}
            source={state.source}
            poster={posterUrl}
            autoPlay={false}
            startSeconds={resumeFrom}
            onTimeUpdate={reportProgress}
            onError={() => {
              // Auto-failover to the next resolvable mirror when playback fails
              if (state.sheicobTry + 1 < resolvableMirrors.length) {
                void startEntry(state.displayIdx, state.sheicobTry + 1);
              } else {
                setState({
                  kind: "error",
                  message:
                    "No pudimos reproducir ningún enlace Sheicob. Probá otro servidor.",
                });
              }
            }}
          />
        )}

        {state.kind === "fallback_iframe" &&
          displayEntries[state.displayIdx]?.kind === "iframe" && (
            <iframe
              key={(displayEntries[state.displayIdx] as { kind: "iframe"; mirror: Mirror }).mirror.id}
              src={(displayEntries[state.displayIdx] as { kind: "iframe"; mirror: Mirror }).mirror.embedUrl}
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

        {/* Resume prompt */}
        {canResume &&
          resumeDecision === "pending" &&
          progress &&
          (state.kind === "preroll" || state.kind === "resolving") && (
            <ResumePrompt
              positionSeconds={progress.positionSeconds}
              onAccept={() => setResumeDecision("accepted")}
              onCancel={() => setResumeDecision("dismissed")}
            />
          )}
      </div>

      {/* ── Title bar + action icons (JKAnime-style) ── */}
      <div className="bg-neutral-900 border-x border-neutral-700/50 px-4 py-2.5 flex items-center gap-3">
        <h2 className="text-sm md:text-base font-bold text-white uppercase truncate flex-1">
          <span className="text-orange-400">{episodeTitle}</span>
        </h2>

        {/* Action icons */}
        <div className="flex items-center gap-1 shrink-0">
          {/* Report broken link */}
          <button
            onClick={handleReport}
            disabled={currentReported || !activeUnderlyingMirror}
            title={currentReported ? "Enlace reportado" : "Reportar enlace roto"}
            className={`p-2 rounded-lg transition-colors ${
              currentReported
                ? "text-green-400 cursor-default"
                : "text-neutral-500 hover:text-red-400 hover:bg-neutral-800"
            } disabled:opacity-40`}
          >
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M3 3l1.664 1.664M21 21l-1.5-1.5m-5.533-1.467a2.625 2.625 0 01-3.513 0l-4.456-4.456a2.625 2.625 0 010-3.513l.793-.793" />
              <path strokeLinecap="round" strokeLinejoin="round" d="M14.121 14.121L7.05 7.05m7.071 7.071l2.829 2.829M7.05 7.05L4.222 4.222M7.05 7.05l3.535-3.536a2.625 2.625 0 013.514 0l3.435 3.435a2.625 2.625 0 010 3.514L14.12 14.12" />
            </svg>
          </button>
        </div>
      </div>

      {/* ── Mirror selector grid (JKAnime table-style) ── */}
      {(() => {
        const hasSeekStreaming = activeMirrors.some(
          (m) => m.providerName === "seekstreaming"
        );
        // When SeekStreaming is present, show only it until the user expands.
        const visibleEntries =
          hasSeekStreaming && !showAllServers
            ? displayEntries.filter(
                (e) => e.kind === "sheicob" || (e.kind === "iframe" && e.mirror.providerName === "seekstreaming")
              )
            : displayEntries;

        return (
          <div className="bg-neutral-900/80 rounded-b-lg border border-t-0 border-neutral-700/50 overflow-hidden">
            <div className="grid grid-cols-3 sm:grid-cols-4 md:grid-cols-5">
              {visibleEntries.map((entry) => {
                // Map back to the real displayEntries index so handleEntrySelect works correctly.
                const i = displayEntries.indexOf(entry);
                const isSheicob = entry.kind === "sheicob";
                const selected = i === currentDisplayIdx;
                const label = isSheicob ? "Sheicob" : entry.mirror.providerName;
                const quality = isSheicob
                  ? resolvableMirrors[0]?.qualityLabel ?? 0
                  : entry.mirror.qualityLabel;
                const key = isSheicob ? "sheicob" : entry.mirror.id;
                return (
                  <button
                    key={key}
                    onClick={() => handleEntrySelect(i)}
                    aria-pressed={selected}
                    className={`px-3 py-3 text-sm font-medium transition-colors text-center border-b border-r border-neutral-700/40 ${
                      selected
                        ? isSheicob
                          ? "bg-gradient-to-br from-amber-500 to-orange-600 text-black"
                          : "bg-orange-600 text-white"
                        : isSheicob
                        ? "bg-amber-950/30 text-amber-300 hover:bg-amber-900/40"
                        : "bg-neutral-800/50 text-neutral-300 hover:bg-neutral-700/60 hover:text-white"
                    }`}
                  >
                    <span className={`block ${isSheicob ? "font-bold tracking-wide" : "capitalize"}`}>
                      {label}
                    </span>
                    {quality > 0 && (
                      <span
                        className={`block text-[10px] mt-0.5 ${
                          selected
                            ? isSheicob ? "text-black/60" : "text-orange-200"
                            : isSheicob ? "text-amber-400/60" : "text-neutral-500"
                        }`}
                      >
                        {quality}p
                      </span>
                    )}
                  </button>
                );
              })}
            </div>
            {/* Toggle: only show when SeekStreaming is present and there are other servers */}
            {hasSeekStreaming && displayEntries.length > visibleEntries.length && (
              <button
                onClick={() => setShowAllServers(true)}
                className="w-full py-2 text-xs text-neutral-400 hover:text-neutral-200 hover:bg-neutral-800/60 transition-colors border-t border-neutral-700/40"
              >
                Ver otros servidores ▼
              </button>
            )}
            {hasSeekStreaming && showAllServers && displayEntries.length > 1 && (
              <button
                onClick={() => setShowAllServers(false)}
                className="w-full py-2 text-xs text-neutral-400 hover:text-neutral-200 hover:bg-neutral-800/60 transition-colors border-t border-neutral-700/40"
              >
                Ocultar otros servidores ▲
              </button>
            )}
          </div>
        );
      })()}
    </div>
  );
}
