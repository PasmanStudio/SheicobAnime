"use client";

import { useEffect, useRef } from "react";

interface PropellerAdsBannerProps {
  zoneId: string;
  width: number;
  height: number;
}

/**
 * PropellerAds Banner component.
 * Loads PropellerAds script and renders the ad zone.
 * Must be used as 'use client' — never in SSR.
 */
export default function PropellerAdsBanner({
  zoneId,
  width,
  height,
}: PropellerAdsBannerProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const scriptLoaded = useRef(false);

  useEffect(() => {
    if (!zoneId || scriptLoaded.current) return;

    const container = containerRef.current;
    if (!container) return;

    // PropellerAds native banner script
    const script = document.createElement("script");
    script.async = true;
    script.src = `//pl.propellerads.com/${zoneId}/invoke.js`;
    script.dataset.cfasync = "false";

    // Create the ad container
    const adDiv = document.createElement("div");
    adDiv.id = `propeller-${zoneId}`;

    container.appendChild(adDiv);
    container.appendChild(script);

    scriptLoaded.current = true;

    return () => {
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
