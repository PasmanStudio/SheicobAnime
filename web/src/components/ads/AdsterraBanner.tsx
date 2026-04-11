"use client";

import { useEffect, useRef } from "react";

interface AdsterraBannerProps {
  zoneId: string;
  width: number;
  height: number;
}

/**
 * Adsterra Native Banner component.
 * Loads Adsterra's ad script and renders in the specified zone.
 * Must be used as 'use client' — never in SSR.
 */
export default function AdsterraBanner({
  zoneId,
  width,
  height,
}: AdsterraBannerProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const scriptLoaded = useRef(false);

  useEffect(() => {
    if (!zoneId || scriptLoaded.current) return;

    const container = containerRef.current;
    if (!container) return;

    // Adsterra native banner script
    const script = document.createElement("script");
    script.async = true;
    script.src = `//www.highperformanceformat.com/${zoneId}/invoke.js`;
    script.dataset.cfasync = "false";

    // Create the ad container div that Adsterra expects
    const adDiv = document.createElement("div");
    adDiv.id = `adsterra-${zoneId}`;

    container.appendChild(adDiv);
    container.appendChild(script);

    scriptLoaded.current = true;

    return () => {
      // Cleanup on unmount
      if (container) {
        container.innerHTML = "";
      }
      scriptLoaded.current = false;
    };
  }, [zoneId]);

  if (!zoneId) {
    return null;
  }

  return (
    <div
      ref={containerRef}
      className="flex items-center justify-center"
      style={{
        minWidth: width,
        minHeight: height,
        maxWidth: "100%",
      }}
      aria-label="Advertisement"
    />
  );
}
