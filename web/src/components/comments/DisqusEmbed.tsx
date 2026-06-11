"use client";

import { useEffect, useRef } from "react";

interface DisqusEmbedProps {
  pageId: string;
  pageUrl: string;
}

declare global {
  interface Window {
    DISQUS?: { reset: (opts: { reload: boolean; config: () => void }) => void };
    disqus_config?: () => void;
  }
}

export default function DisqusEmbed({ pageId, pageUrl }: DisqusEmbedProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const shortname = process.env.NEXT_PUBLIC_DISQUS_SHORTNAME ?? "";

  useEffect(() => {
    if (!shortname) return;

    const config = function (this: { page: { url: string; identifier: string } }) {
      this.page.url = pageUrl;
      this.page.identifier = pageId;
    };

    // If Disqus is already loaded, reset it for the new page
    if (window.DISQUS) {
      window.DISQUS.reset({ reload: true, config });
      return;
    }

    // First load — inject the embed script
    window.disqus_config = config;
    const script = document.createElement("script");
    script.src = `https://${encodeURIComponent(shortname)}.disqus.com/embed.js`;
    script.setAttribute("data-timestamp", String(Date.now()));
    script.async = true;
    containerRef.current?.appendChild(script);

    return () => {
      // Cleanup on unmount
      const disqusThread = document.getElementById("disqus_thread");
      if (disqusThread) disqusThread.innerHTML = "";
    };
  }, [shortname, pageId, pageUrl]);

  if (!shortname) {
    return (
      <p className="text-ink-3 text-sm">
        Comments unavailable — DISQUS_SHORTNAME not configured.
      </p>
    );
  }

  return (
    <div ref={containerRef}>
      <div id="disqus_thread" />
      <noscript>
        <p className="text-ink-3 text-sm">
          Habilita JavaScript para ver los comentarios de Disqus.
        </p>
      </noscript>
    </div>
  );
}
