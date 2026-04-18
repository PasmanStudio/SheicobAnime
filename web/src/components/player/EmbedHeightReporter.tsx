"use client";

import { useEffect } from "react";

/**
 * Lives inside the /embed iframe and publishes its document height to the
 * parent frame via postMessage so EmbeddedPlayerFrame can size the iframe
 * to exactly fit the video + mirror selector (no inner scrollbar, no clip).
 *
 * Only posts to same-origin parent. Silent no-op if the page is not iframed.
 */
export default function EmbedHeightReporter() {
  useEffect(() => {
    const w = globalThis as unknown as Window;
    if (w.parent === w) return; // not iframed

    const targetOrigin: string = w.location.origin;
    const post = () => {
      const height = Math.max(
        document.documentElement.scrollHeight,
        document.body.scrollHeight,
      );
      w.parent.postMessage({ type: "sheicob:resize", height }, targetOrigin);
    };

    post();

    const ro = new ResizeObserver(() => post());
    ro.observe(document.documentElement);
    ro.observe(document.body);

    w.addEventListener("load", post);
    return () => {
      ro.disconnect();
      w.removeEventListener("load", post);
    };
  }, []);

  return null;
}
