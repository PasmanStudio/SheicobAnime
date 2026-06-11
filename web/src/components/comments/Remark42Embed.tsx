"use client";

import { useEffect, useRef } from "react";

interface Remark42EmbedProps {
  pageId: string;
  pageUrl: string;
}

declare global {
  // eslint-disable-next-line @typescript-eslint/no-empty-object-type
  interface Window {
    remark_config?: {
      host: string;
      site_id: string;
      url: string;
      page_title?: string;
    };
    REMARK42?: {
      changeUrl: (url: string) => void;
      destroy: () => void;
    };
  }
}

export default function Remark42Embed({ pageId, pageUrl }: Remark42EmbedProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const remark42Url = process.env.NEXT_PUBLIC_REMARK42_URL ?? "";

  useEffect(() => {
    if (!remark42Url) return;

    window.remark_config = {
      host: remark42Url,
      site_id: "sheicob",
      url: pageUrl,
    };

    // If already loaded, update URL
    if (window.REMARK42) {
      window.REMARK42.changeUrl(pageUrl);
      return;
    }

    // First load — inject scripts
    const components = ["embed"];
    for (const component of components) {
      const script = document.createElement("script");
      script.src = `${remark42Url}/web/${component}.mjs`;
      script.type = "module";
      script.defer = true;
      containerRef.current?.appendChild(script);
    }

    return () => {
      if (window.REMARK42) {
        window.REMARK42.destroy();
      }
    };
    // pageId used as key to re-render on page change
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [remark42Url, pageId, pageUrl]);

  if (!remark42Url) {
    return (
      <p className="text-ink-3 text-sm">
        Comments unavailable — REMARK42_URL not configured.
      </p>
    );
  }

  return (
    <div ref={containerRef}>
      <div id="remark42" />
    </div>
  );
}
