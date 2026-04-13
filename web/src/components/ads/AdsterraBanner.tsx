"use client";

import { useEffect, useRef } from "react";

interface AdsterraBannerProps {
  /** Full script src URL from Adsterra (e.g. https://plXXX.../HASH/invoke.js) */
  scriptSrc: string;
  /** Container hash from Adsterra (the HASH part used in container-HASH div) */
  containerHash: string;
  width: number;
  height: number;
}

/**
 * Adsterra Native Banner component.
 * Loads Adsterra's native banner script and renders in the expected container.
 * Must be used as 'use client' — never in SSR.
 */
export default function AdsterraBanner({
  scriptSrc,
  containerHash,
  width,
  height,
}: AdsterraBannerProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const scriptLoaded = useRef(false);

  useEffect(() => {
    if (!scriptSrc || !containerHash || scriptLoaded.current) return;

    const container = containerRef.current;
    if (!container) return;

    // Create the container div that Adsterra expects (id="container-HASH")
    const adDiv = document.createElement("div");
    adDiv.id = `container-${containerHash}`;

    // Adsterra native banner invoke script
    const script = document.createElement("script");
    script.async = true;
    script.dataset.cfasync = "false";
    script.src = scriptSrc;

    container.appendChild(adDiv);
    container.appendChild(script);

    scriptLoaded.current = true;

    return () => {
      if (container) {
        container.innerHTML = "";
      }
      scriptLoaded.current = false;
    };
  }, [scriptSrc, containerHash]);

  if (!scriptSrc || !containerHash) {
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
