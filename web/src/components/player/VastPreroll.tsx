"use client";

import { useCallback, useEffect, useRef, useState } from "react";

/**
 * VAST preroll using direct XML parsing + native <video>.
 *
 * Replaces the Google IMA SDK approach which required a user gesture to call
 * adContainer.initialize(). This implementation fetches the VAST XML, parses
 * out the MediaFile URL, and plays the ad video directly — no third-party SDK,
 * no gesture requirement.
 *
 * Inspired by how JKAnime handles AdAngle VAST 3.0 tags.
 *
 * Fails open: on any error / timeout / empty VAST the viewer sees the main
 * video — a broken ad must never block content.
 */

const HARD_TIMEOUT_MS = 12_000;
const DEFAULT_SKIP_SECONDS = 5;
const DEBUG = typeof window !== "undefined" && new URLSearchParams(window.location.search).has("vastDebug");

function vastLog(msg: string, ...args: unknown[]) {
  if (DEBUG) console.warn(`[VAST] ${msg}`, ...args);
}

/* ------------------------------------------------------------------ */
/*  VAST XML types                                                     */
/* ------------------------------------------------------------------ */

interface VastAd {
  mediaUrl: string;
  clickThrough: string | null;
  skipOffsetSeconds: number;
  impressionUrls: string[];
  trackingEvents: Map<string, string[]>;
}

/* ------------------------------------------------------------------ */
/*  Parsing helpers                                                    */
/* ------------------------------------------------------------------ */

function parseSkipOffset(attr: string | null): number | null {
  if (!attr) return null;
  const parts = attr.split(":");
  if (parts.length === 3) {
    return (
      Number.parseInt(parts[0], 10) * 3600 +
      Number.parseInt(parts[1], 10) * 60 +
      Number.parseInt(parts[2], 10)
    );
  }
  const n = Number.parseInt(attr, 10);
  return Number.isFinite(n) ? n : null;
}

function parseVastXml(xml: string): VastAd | null {
  try {
    const doc = new DOMParser().parseFromString(xml, "text/xml");
    if (doc.querySelector("parsererror")) return null;

    const ad = doc.querySelector("Ad");
    if (!ad) return null;

    const linear = ad.querySelector("Linear");
    if (!linear) return null;

    // Media file — prefer progressive MP4
    const mediaFiles = Array.from(linear.querySelectorAll("MediaFile"));
    let mediaUrl = "";
    for (const mf of mediaFiles) {
      const url = mf.textContent?.trim();
      const type = mf.getAttribute("type") ?? "";
      if (url && type.includes("mp4")) {
        mediaUrl = url;
        break;
      }
    }
    if (!mediaUrl) mediaUrl = mediaFiles[0]?.textContent?.trim() ?? "";
    if (!mediaUrl) return null;

    const clickThrough =
      ad.querySelector("ClickThrough")?.textContent?.trim() ?? null;

    const skipOffsetSeconds =
      parseSkipOffset(linear.getAttribute("skipoffset")) ?? DEFAULT_SKIP_SECONDS;

    const impressionUrls: string[] = [];
    for (const el of Array.from(ad.querySelectorAll("Impression"))) {
      const url = el.textContent?.trim();
      if (url) impressionUrls.push(url);
    }

    const trackingEvents = new Map<string, string[]>();
    for (const tr of Array.from(linear.querySelectorAll("TrackingEvents > Tracking"))) {
      const event = tr.getAttribute("event");
      const url = tr.textContent?.trim();
      if (event && url) {
        const list = trackingEvents.get(event) ?? [];
        list.push(url);
        trackingEvents.set(event, list);
      }
    }

    return {
      mediaUrl,
      clickThrough,
      skipOffsetSeconds,
      impressionUrls,
      trackingEvents,
    };
  } catch (err) {
    vastLog("XML parse threw", err);
    return null;
  }
}

function firePixels(urls: string[]) {
  for (const url of urls) {
    const img = new Image();
    img.src = url;
  }
}

/* ------------------------------------------------------------------ */
/*  Component                                                          */
/* ------------------------------------------------------------------ */

interface VastPrerollProps {
  vastUrl: string;
  onComplete: () => void;
  /** Called when VAST returns no fill (empty XML / fetch error). Parent can show fallback ad. */
  onNoFill?: () => void;
}

export default function VastPreroll({
  vastUrl,
  onComplete,
  onNoFill,
}: Readonly<VastPrerollProps>) {
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const completedRef = useRef(false);
  const firedRef = useRef(new Set<string>());

  const [ad, setAd] = useState<VastAd | null>(null);
  const [adStarted, setAdStarted] = useState(false);
  const [canSkip, setCanSkip] = useState(false);
  const [countdown, setCountdown] = useState<number | null>(null);
  const [isMuted, setIsMuted] = useState(true);

  const complete = useCallback(() => {
    if (completedRef.current) return;
    completedRef.current = true;
    try {
      videoRef.current?.pause();
    } catch {
      /* ignore */
    }
    onComplete();
  }, [onComplete]);

  // When VAST has no ad fill, delegate to the parent (onNoFill) so it can show
  // a fallback ad.  Only call complete() when there's no onNoFill handler,
  // otherwise the parent will manage the transition via its own skip button.
  const noFillOrComplete = useCallback(() => {
    if (onNoFill) {
      onNoFill();
    } else {
      complete();
    }
  }, [onNoFill, complete]);

  /* ---------- Fetch & parse VAST ---------- */
  useEffect(() => {
    if (!vastUrl) {
      vastLog("No VAST URL provided");
      noFillOrComplete();
      return;
    }

    let cancelled = false;
    const hardTimeout = setTimeout(() => {
      vastLog("Hard timeout reached (%dms)", HARD_TIMEOUT_MS);
      noFillOrComplete();
    }, HARD_TIMEOUT_MS);

    (async () => {
      try {
        vastLog("Fetching VAST from %s", vastUrl);
        const res = await fetch(vastUrl, { mode: "cors", credentials: "omit" });
        if (cancelled) return;
        vastLog("VAST response status=%d", res.status);
        if (!res.ok) {
          vastLog("VAST fetch not OK — skipping");
          noFillOrComplete();
          return;
        }
        const xml = await res.text();
        if (cancelled) return;
        vastLog("VAST XML length=%d, first 200 chars: %s", xml.length, xml.slice(0, 200));

        const parsed = parseVastXml(xml);
        if (!parsed) {
          vastLog("VAST parsed to null (no <Ad>, no <MediaFile>, or parse error)");
          noFillOrComplete();
          return;
        }

        vastLog("VAST ad found: media=%s, skip=%ds, impressions=%d",
          parsed.mediaUrl, parsed.skipOffsetSeconds, parsed.impressionUrls.length);
        setAd(parsed);
        firePixels(parsed.impressionUrls);
      } catch (err) {
        vastLog("VAST fetch/parse error", err);
        if (!cancelled) {
          noFillOrComplete();
        }
      }
    })();

    return () => {
      cancelled = true;
      clearTimeout(hardTimeout);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [vastUrl]);

  /* ---------- Play the ad video once parsed ---------- */
  useEffect(() => {
    if (!ad) return;
    const video = videoRef.current;
    if (!video) return;

    vastLog("Loading ad creative: %s", ad.mediaUrl);
    video.src = ad.mediaUrl;
    video.load();
    video.muted = true;
    video.play().catch((err) => {
      // Autoplay truly blocked → skip to content
      vastLog("Autoplay blocked", err);
      complete();
    });
  }, [ad, complete]);

  /* ---------- Track progress / fire pixels ---------- */
  useEffect(() => {
    if (!ad) return;
    const video = videoRef.current;
    if (!video) return;

    const onPlaying = () => {
      setAdStarted(true);
      if (!firedRef.current.has("start")) {
        firedRef.current.add("start");
        firePixels(ad.trackingEvents.get("start") ?? []);
        firePixels(ad.trackingEvents.get("creativeView") ?? []);
      }
    };

    const onTimeUpdate = () => {
      const t = video.currentTime;
      const d = video.duration;
      if (!d || !Number.isFinite(d)) return;

      const remaining = Math.ceil(ad.skipOffsetSeconds - t);
      if (remaining > 0) {
        setCountdown(remaining);
        setCanSkip(false);
      } else {
        setCountdown(null);
        setCanSkip(true);
      }

      const pct = t / d;
      const fire = (key: string, threshold: number) => {
        if (pct >= threshold && !firedRef.current.has(key)) {
          firedRef.current.add(key);
          firePixels(ad.trackingEvents.get(key) ?? []);
        }
      };
      fire("firstQuartile", 0.25);
      fire("midpoint", 0.5);
      fire("thirdQuartile", 0.75);
    };

    const onEnded = () => {
      if (!firedRef.current.has("complete")) {
        firedRef.current.add("complete");
        firePixels(ad.trackingEvents.get("complete") ?? []);
      }
      complete();
    };

    const onError = () => complete();

    video.addEventListener("playing", onPlaying);
    video.addEventListener("timeupdate", onTimeUpdate);
    video.addEventListener("ended", onEnded);
    video.addEventListener("error", onError);

    return () => {
      video.removeEventListener("playing", onPlaying);
      video.removeEventListener("timeupdate", onTimeUpdate);
      video.removeEventListener("ended", onEnded);
      video.removeEventListener("error", onError);
    };
  }, [ad, complete]);

  /* ---------- User actions ---------- */

  const handleSkip = useCallback(() => {
    if (!firedRef.current.has("skip")) {
      firedRef.current.add("skip");
      firePixels(ad?.trackingEvents.get("skip") ?? []);
    }
    complete();
  }, [ad, complete]);

  const handleAdClick = useCallback(() => {
    if (!ad?.clickThrough) return;
    firePixels(ad.trackingEvents.get("click") ?? []);
    window.open(ad.clickThrough, "_blank", "noopener,noreferrer");
  }, [ad]);

  const toggleMute = useCallback(() => {
    const video = videoRef.current;
    if (!video) return;
    video.muted = !video.muted;
    if (!video.muted) video.volume = 0.5;
    setIsMuted(video.muted);
  }, []);

  /* ---------- Render ---------- */
  return (
    <div className="absolute inset-0 bg-black z-10">
      <video
        ref={videoRef}
        className="absolute inset-0 w-full h-full object-contain cursor-pointer"
        playsInline
        muted
        onClick={handleAdClick}
      />

      {!adStarted && (
        <div className="absolute inset-0 flex flex-col items-center justify-center gap-3 pointer-events-none">
          <div className="w-10 h-10 border-4 border-orange-500 border-t-transparent rounded-full animate-spin" />
          <p className="text-xs text-neutral-400 uppercase tracking-wide">
            Cargando publicidad…
          </p>
        </div>
      )}

      <div className="absolute top-2 left-3 px-2 py-1 bg-black/70 rounded text-[10px] uppercase tracking-wide text-neutral-300 pointer-events-none">
        Publicidad
      </div>

      {adStarted && (
        <button
          onClick={toggleMute}
          aria-label={isMuted ? "Activar sonido" : "Silenciar"}
          className="absolute bottom-3 left-3 min-w-[40px] min-h-[40px] px-3 py-2 bg-black/70 text-white rounded text-xs hover:bg-black/90 transition-colors"
        >
          {isMuted ? "🔇 Sonido" : "🔊 Sonido"}
        </button>
      )}

      <div className="absolute top-2 right-2">
        {countdown !== null && countdown > 0 ? (
          <span className="px-3 py-2 bg-black/70 text-neutral-300 rounded text-xs tabular-nums">
            Saltar en {countdown}s
          </span>
        ) : canSkip ? (
          <button
            onClick={handleSkip}
            aria-label="Saltar publicidad"
            className="min-w-[44px] min-h-[40px] px-4 py-2 bg-white/95 text-black rounded font-semibold text-xs hover:bg-white transition-colors focus:outline-none focus:ring-2 focus:ring-orange-400"
          >
            Saltar ▶
          </button>
        ) : null}
      </div>
    </div>
  );
}
