"use client";

import { useCallback, useEffect, useRef, useState } from "react";

/**
 * VAST preroll powered by Google IMA SDK.
 *
 * - Loads ima3.js lazily (no cost when VAST URL is not configured).
 * - Fails open: on any error / timeout / missing URL, calls onComplete() so the
 *   main video plays anyway. A broken ad must never block content.
 * - Shows branded "Publicidad" overlay while the ad is active, with a hard
 *   "Saltar" escape hatch after 5 s.
 */

const IMA_SDK_URL = "https://imasdk.googleapis.com/js/sdkloader/ima3.js";
const HARD_TIMEOUT_MS = 10_000;       // max wait for IMA to start an ad
const MIN_SKIP_DELAY_MS = 5_000;      // show "Saltar" button after this

// Minimal ambient shim so we don't pull the full google/ima @types package.
interface ImaGlobal {
  AdDisplayContainer: new (container: HTMLElement, video: HTMLVideoElement) => {
    initialize(): void;
    destroy(): void;
  };
  AdsLoader: new (container: unknown) => {
    addEventListener(type: string, handler: (evt: unknown) => void): void;
    requestAds(req: unknown): void;
    destroy(): void;
    contentComplete(): void;
  };
  AdsRequest: new () => Record<string, unknown>;
  AdsManagerLoadedEvent: { Type: { ADS_MANAGER_LOADED: string } };
  AdErrorEvent: { Type: { AD_ERROR: string } };
  AdEvent: {
    Type: {
      STARTED: string;
      COMPLETE: string;
      ALL_ADS_COMPLETED: string;
      SKIPPED: string;
      USER_CLOSE: string;
    };
  };
  ViewMode: { NORMAL: string };
}

declare global {
  interface Window {
    google?: { ima?: ImaGlobal };
  }
}

let imaLoadPromise: Promise<ImaGlobal | null> | null = null;

function loadImaSdk(): Promise<ImaGlobal | null> {
  if (typeof window === "undefined") return Promise.resolve(null);
  if (window.google?.ima) return Promise.resolve(window.google.ima);
  if (imaLoadPromise) return imaLoadPromise;

  imaLoadPromise = new Promise((resolve) => {
    const existing = document.querySelector<HTMLScriptElement>(
      `script[src="${IMA_SDK_URL}"]`
    );
    if (existing) {
      existing.addEventListener("load", () => resolve(window.google?.ima ?? null));
      existing.addEventListener("error", () => resolve(null));
      return;
    }

    const script = document.createElement("script");
    script.src = IMA_SDK_URL;
    script.async = true;
    script.onload = () => resolve(window.google?.ima ?? null);
    script.onerror = () => {
      imaLoadPromise = null; // allow retry later
      resolve(null);
    };
    document.head.appendChild(script);
  });

  return imaLoadPromise;
}

interface VastPrerollProps {
  vastUrl: string;
  onComplete: () => void;
}

export default function VastPreroll({ vastUrl, onComplete }: Readonly<VastPrerollProps>) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const cleanupRef = useRef<(() => void) | null>(null);
  const completedRef = useRef(false);

  const [adStarted, setAdStarted] = useState(false);
  const [canSkip, setCanSkip] = useState(false);

  const complete = useCallback(() => {
    if (completedRef.current) return;
    completedRef.current = true;
    cleanupRef.current?.();
    cleanupRef.current = null;
    onComplete();
  }, [onComplete]);

  useEffect(() => {
    let cancelled = false;

    if (!vastUrl) {
      complete();
      return;
    }

    // Hard timeout — if IMA never says anything, play the video anyway.
    const hardTimeout = setTimeout(complete, HARD_TIMEOUT_MS);
    const skipTimer = setTimeout(() => setCanSkip(true), MIN_SKIP_DELAY_MS);

    (async () => {
      const ima = await loadImaSdk();
      if (cancelled || !ima) {
        complete();
        return;
      }

      const container = containerRef.current;
      const video = videoRef.current;
      if (!container || !video) {
        complete();
        return;
      }

      let adsManager: {
        addEventListener: (type: string, handler: (evt: unknown) => void) => void;
        init: (w: number, h: number, mode: string) => void;
        start: () => void;
        destroy: () => void;
        resize: (w: number, h: number, mode: string) => void;
      } | null = null;

      const adContainer = new ima.AdDisplayContainer(container, video);
      // initialize() MUST run before playback events — we call it right after
      // user landed on the player page (implicit gesture: click-to-play flow).
      try { adContainer.initialize(); } catch { /* some browsers require a gesture */ }

      const loader = new ima.AdsLoader(adContainer);

      const onResize = () => {
        if (!adsManager || !container) return;
        try {
          adsManager.resize(
            container.clientWidth,
            container.clientHeight,
            ima.ViewMode.NORMAL
          );
        } catch { /* ignore */ }
      };
      window.addEventListener("resize", onResize);

      cleanupRef.current = () => {
        clearTimeout(hardTimeout);
        clearTimeout(skipTimer);
        window.removeEventListener("resize", onResize);
        try { adsManager?.destroy(); } catch { /* ignore */ }
        try { loader.destroy(); } catch { /* ignore */ }
        try { adContainer.destroy(); } catch { /* ignore */ }
      };

      loader.addEventListener(
        ima.AdsManagerLoadedEvent.Type.ADS_MANAGER_LOADED,
        (evt: unknown) => {
          if (cancelled || completedRef.current) return;
          try {
            const ev = evt as { getAdsManager: (v: HTMLVideoElement) => typeof adsManager };
            adsManager = ev.getAdsManager(video);
            if (!adsManager) { complete(); return; }

            const onDone = () => complete();
            adsManager.addEventListener(ima.AdEvent.Type.STARTED, () => setAdStarted(true));
            adsManager.addEventListener(ima.AdEvent.Type.COMPLETE, onDone);
            adsManager.addEventListener(ima.AdEvent.Type.ALL_ADS_COMPLETED, onDone);
            adsManager.addEventListener(ima.AdEvent.Type.SKIPPED, onDone);
            adsManager.addEventListener(ima.AdEvent.Type.USER_CLOSE, onDone);
            adsManager.addEventListener(ima.AdErrorEvent.Type.AD_ERROR, onDone);

            adsManager.init(
              container.clientWidth,
              container.clientHeight,
              ima.ViewMode.NORMAL
            );
            adsManager.start();
          } catch {
            complete();
          }
        }
      );

      loader.addEventListener(ima.AdErrorEvent.Type.AD_ERROR, () => complete());

      try {
        const req = new ima.AdsRequest();
        req.adTagUrl = vastUrl;
        req.linearAdSlotWidth = container.clientWidth;
        req.linearAdSlotHeight = container.clientHeight;
        req.nonLinearAdSlotWidth = container.clientWidth;
        req.nonLinearAdSlotHeight = 150;
        loader.requestAds(req);
      } catch {
        complete();
      }
    })();

    return () => {
      cancelled = true;
      cleanupRef.current?.();
      cleanupRef.current = null;
    };
    // complete / vastUrl are the only real deps; complete is stable
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [vastUrl]);

  return (
    <div className="absolute inset-0 bg-black z-10">
      {/* IMA renders ad creatives into this container */}
      <div ref={containerRef} className="absolute inset-0" />
      {/* Hidden content video IMA needs as a handle */}
      <video ref={videoRef} className="absolute inset-0 w-full h-full" playsInline muted />

      {/* Waiting overlay, until IMA fires STARTED */}
      {!adStarted && (
        <div className="absolute inset-0 flex flex-col items-center justify-center gap-3 pointer-events-none">
          <div className="w-10 h-10 border-4 border-orange-500 border-t-transparent rounded-full animate-spin" />
          <p className="text-xs text-neutral-400 uppercase tracking-wide">Cargando publicidad…</p>
        </div>
      )}

      <div className="absolute top-2 left-3 px-2 py-1 bg-black/70 rounded text-[10px] uppercase tracking-wide text-neutral-300">
        Publicidad
      </div>

      {canSkip && (
        <button
          onClick={complete}
          aria-label="Saltar publicidad"
          className="absolute top-2 right-2 min-w-[44px] min-h-[40px] px-4 py-2 bg-white/95 text-black rounded font-semibold text-xs hover:bg-white transition-colors focus:outline-none focus:ring-2 focus:ring-orange-400"
        >
          Saltar ▶
        </button>
      )}
    </div>
  );
}
