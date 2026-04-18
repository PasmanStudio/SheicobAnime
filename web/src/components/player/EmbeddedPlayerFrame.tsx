"use client";

import { useEffect, useRef, useState } from "react";

/**
 * Same-origin iframe wrapper around /embed/[episodeId]. Isolates the player's
 * DOM and JavaScript runtime from the host page so naïve scrapers can't reach
 * the &lt;video&gt; element, resolved source state, or in-flight requests via
 * the top frame's `document`.
 *
 * Network-level protection already exists (AES-GCM encrypted proxy tokens) —
 * this adds DOM/JS isolation on top of that.
 *
 * The iframe auto-resizes to fit its content (video + mirror selector) via a
 * postMessage "sheicob:resize" bridge published by the embed layout.
 */

// Initial width:height aspect ratio before postMessage provides the real
// content height. Tuned to cover 16:9 video + one row of mirror buttons +
// report link so the embed is never visibly cropped on first paint.
const INITIAL_ASPECT = 16 / 11;

interface Props {
  episodeId: string;
  episodeTitle: string;
}

export default function EmbeddedPlayerFrame({ episodeId, episodeTitle }: Readonly<Props>) {
  const iframeRef = useRef<HTMLIFrameElement | null>(null);
  const [height, setHeight] = useState<number | null>(null);

  useEffect(() => {
    const onMessage = (e: MessageEvent) => {
      // Only accept messages from our own origin to keep the bridge trustable.
      if (globalThis.location !== undefined && e.origin !== globalThis.location.origin) return;
      const data = e.data as { type?: string; height?: number } | null;
      if (data?.type !== "sheicob:resize" || typeof data.height !== "number") return;
      setHeight(Math.max(240, Math.round(data.height)));
    };
    globalThis.addEventListener("message", onMessage);
    return () => globalThis.removeEventListener("message", onMessage);
  }, []);

  return (
    <div
      className="w-full rounded-lg overflow-hidden shadow-2xl bg-black"
      style={
        height
          ? { height: `${height}px` }
          : { aspectRatio: `${INITIAL_ASPECT}` }
      }
    >
      <iframe
        ref={iframeRef}
        src={`/embed/${episodeId}`}
        title={episodeTitle}
        className="w-full h-full border-0 block"
        allow="autoplay; fullscreen; encrypted-media; picture-in-picture"
        allowFullScreen
        loading="eager"
      />
    </div>
  );
}
