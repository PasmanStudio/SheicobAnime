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

  useEffect(() => {
    episodeIdRef.current = episodeId;
  }, [episodeId]);

  // Fetch existing progress once per episode
  useEffect(() => {
    if (!episodeId) {
      setInitial(null);
      setReady(true);
      return;
    }
    let cancelled = false;
    setReady(false);
    getWatchProgress(episodeId)
      .then((p) => {
        if (cancelled) return;
        setInitial(p);
      })
      .catch(() => {
        if (cancelled) return;
        setInitial(null);
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
