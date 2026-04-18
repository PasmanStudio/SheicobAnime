"use client";

import { getWatchProgress, updateWatchProgress } from "@/lib/api";
import type { WatchProgress } from "@/lib/types";
import { useCallback, useEffect, useRef, useState } from "react";

/** How often to persist progress to the server while playing. */
const SAVE_INTERVAL_MS = 15_000;
/** Don't prompt to resume if the viewer was essentially at the start. */
const MIN_RESUMABLE_SECONDS = 10;
/** Don't prompt if they're within 30s of the end — just let them watch the outro. */
const END_BUFFER_SECONDS = 30;
const LS_PREFIX = "sheicob:wp:";
const LS_SAVE_INTERVAL_MS = 5_000;

export interface UseWatchProgressResult {
  /** `null` while loading, or if no prior progress exists. */
  initial: WatchProgress | null;
  /** `true` once the initial fetch has settled. */
  ready: boolean;
  /** Should a resume prompt be shown? Consult after `ready`. */
  canResume: boolean;
  /** Call from the player's timeupdate (throttled internally). */
  reportProgress: (position: number, duration: number) => void;
  /** Force-save now (e.g. on pause, page hide). */
  flush: () => void;
}

export function useWatchProgress(episodeId: string | null): UseWatchProgressResult {
  const [initial, setInitial] = useState<WatchProgress | null>(null);
  const [ready, setReady] = useState(false);

  // Ref storage so the reporter closure remains stable across renders
  const lastSavedAtRef = useRef<number>(0);
  const pendingRef = useRef<{ position: number; duration: number } | null>(null);
  const inFlightRef = useRef(false);
  const episodeIdRef = useRef(episodeId);
  const lastLocalSaveRef = useRef<number>(0);

  useEffect(() => {
    episodeIdRef.current = episodeId;
  }, [episodeId]);

  // Fetch existing progress — localStorage first (instant, zero-cost like JKAnime),
  // then API as backup (may have data from other devices).
  useEffect(() => {
    if (!episodeId) {
      setInitial(null);
      setReady(true);
      return;
    }
    // 1. localStorage — instant
    let localData: WatchProgress | null = null;
    try {
      const raw = localStorage.getItem(`${LS_PREFIX}${episodeId}`);
      if (raw) {
        const obj = JSON.parse(raw) as { p: number; d: number; t: number };
        if (obj.p > 0 && obj.d > 0) {
          localData = {
            episodeId,
            seriesSlug: "",
            positionSeconds: obj.p,
            durationSeconds: obj.d,
            completed: false,
            updatedAt: new Date(obj.t).toISOString(),
          };
          setInitial(localData);
          setReady(true);
        }
      }
    } catch { /* localStorage unavailable */ }

    // 2. API — may override with newer data
    let cancelled = false;
    if (!localData) setReady(false);
    getWatchProgress(episodeId)
      .then((p) => {
        if (cancelled) return;
        if (p) {
          const apiTime = new Date(p.updatedAt).getTime();
          const localTime = localData ? new Date(localData.updatedAt).getTime() : 0;
          if (!localData || apiTime > localTime) {
            setInitial(p);
          }
        }
      })
      .catch(() => {
        // API failed — localStorage data (if any) is already set
      })
      .finally(() => {
        if (!cancelled) setReady(true);
      });
    return () => {
      cancelled = true;
    };
  }, [episodeId]);

  const doSave = useCallback(async (position: number, duration: number) => {
    const id = episodeIdRef.current;
    if (!id || inFlightRef.current) return;
    inFlightRef.current = true;
    try {
      await updateWatchProgress(id, Math.round(position), Math.round(duration));
      lastSavedAtRef.current = Date.now();
    } catch {
      // Progress tracking is best-effort — silently tolerate failures
    } finally {
      inFlightRef.current = false;
    }
  }, []);

  const reportProgress = useCallback(
    (position: number, duration: number) => {
      if (!Number.isFinite(position) || !Number.isFinite(duration) || duration <= 0) return;
      pendingRef.current = { position, duration };
      // localStorage — fast local save every 5s (zero-cost, like JKAnime)
      const localElapsed = Date.now() - lastLocalSaveRef.current;
      if (localElapsed >= LS_SAVE_INTERVAL_MS) {
        try {
          const id = episodeIdRef.current;
          if (id) {
            localStorage.setItem(`${LS_PREFIX}${id}`, JSON.stringify({
              p: Math.round(position), d: Math.round(duration), t: Date.now(),
            }));
            lastLocalSaveRef.current = Date.now();
          }
        } catch { /* quota exceeded or unavailable */ }
      }
      // API — remote save every 15s
      const elapsed = Date.now() - lastSavedAtRef.current;
      if (elapsed >= SAVE_INTERVAL_MS) {
        void doSave(position, duration);
      }
    },
    [doSave]
  );

  const flush = useCallback(() => {
    const p = pendingRef.current;
    if (!p) return;
    try {
      const id = episodeIdRef.current;
      if (id) {
        localStorage.setItem(`${LS_PREFIX}${id}`, JSON.stringify({
          p: Math.round(p.position), d: Math.round(p.duration), t: Date.now(),
        }));
      }
    } catch { /* ignored */ }
    void doSave(p.position, p.duration);
  }, [doSave]);

  // Persist on unload / visibility change so we don't lose the last few seconds
  useEffect(() => {
    const onHide = () => flush();
    globalThis.addEventListener("pagehide", onHide);
    globalThis.addEventListener("beforeunload", onHide);
    return () => {
      globalThis.removeEventListener("pagehide", onHide);
      globalThis.removeEventListener("beforeunload", onHide);
    };
  }, [flush]);

  const canResume = Boolean(
    ready &&
      initial &&
      initial.positionSeconds >= MIN_RESUMABLE_SECONDS &&
      initial.durationSeconds > 0 &&
      initial.positionSeconds < initial.durationSeconds - END_BUFFER_SECONDS &&
      !initial.completed
  );

  return { initial, ready, canResume, reportProgress, flush };
}
