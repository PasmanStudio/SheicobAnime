"use client";

import { useState, useEffect, useRef } from "react";
import dynamic from "next/dynamic";
import {
  AD_CONFIG,
  ADSTERRA_NATIVE,
  getAdProvider,
  hasValidZone,
  type AdPlacement,
} from "@/lib/ad-config";
import { hasAdConsent } from "./ConsentBanner";

// Lazy load provider-specific components to reduce bundle size
const AdsterraBanner = dynamic(() => import("./AdsterraBanner"), {
  ssr: false,
});

const PropellerAdsBanner = dynamic(() => import("./PropellerAdsBanner"), {
  ssr: false,
});

export interface AdSlotProps {
  /** Placement key from AD_CONFIG */
  placement: AdPlacement;
  /** Optional className for the wrapper */
  className?: string;
}

/**
 * AdSlot — ONLY ad component to use in pages.
 * Uses AD_CONFIG for zone IDs and AD_PROVIDER for network selection.
 *
 * Features:
 * - Placements podados via `enabled: false` (doc 2) — render null
 * - Altura SIEMPRE reservada antes de que cargue el script (cero CLS)
 * - Formato por dispositivo: desktop 728×90, móvil 320×100 / 300×250
 * - Lazy-load bajo el fold via IntersectionObserver
 * - Label mono "PUBLICIDAD" — honesto y ordenado
 * - Consent-aware (no ads without GDPR consent)
 *
 * Contract: AD_CONFIG is the ONLY place with ad zone IDs.
 */
export default function AdSlot({ placement, className }: AdSlotProps) {
  const [mounted, setMounted] = useState(false);
  const [hasConsent, setHasConsent] = useState(false);
  const [isMobile, setIsMobile] = useState(false);
  const [inView, setInView] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    setMounted(true);
    setHasConsent(hasAdConsent());
    const mq = window.matchMedia("(max-width: 767px)");
    setIsMobile(mq.matches);
    const onChange = (e: MediaQueryListEvent) => setIsMobile(e.matches);
    mq.addEventListener("change", onChange);
    return () => mq.removeEventListener("change", onChange);
  }, []);

  // Lazy-load: solo invocar el script cuando el slot se acerca al viewport
  useEffect(() => {
    const el = containerRef.current;
    if (!el || inView) return;
    if (!("IntersectionObserver" in window)) {
      setInView(true);
      return;
    }
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          setInView(true);
          observer.disconnect();
        }
      },
      { rootMargin: "300px" },
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, [inView, mounted]);

  const config = AD_CONFIG[placement];

  // Placement podado: nada, en ningún modo
  if (!config.enabled) {
    return null;
  }

  // SSR: render nothing to avoid hydration issues
  if (!mounted) {
    return null;
  }

  const provider = getAdProvider();
  const width = isMobile ? config.mobileWidth : config.width;
  const height = isMobile ? config.mobileHeight : config.height;

  // Stub mode: show placeholder in development
  if (provider === "stub") {
    return (
      <div className={`mx-auto w-full max-w-[740px] ${className ?? ""}`}>
        <div className="mb-1 font-mono text-[9px] tracking-[0.14em] text-ink-3">PUBLICIDAD</div>
        <div
          className="flex items-center justify-center rounded-btn border border-dashed border-line-2 bg-abyss-1 font-mono text-[11px] text-ink-3"
          style={{ minHeight: height, maxWidth: "100%" }}
        >
          Ad: {placement} · {width}×{height}
        </div>
      </div>
    );
  }

  // No consent: don't load ads
  if (!hasConsent) {
    return null;
  }

  // No valid zone: skip silently
  if (!hasValidZone(placement)) {
    return null;
  }

  // Render provider-specific banner — contenedor del design system con
  // altura reservada (cero CLS) y label PUBLICIDAD arriba.
  return (
    <div ref={containerRef} className={`mx-auto w-full max-w-[740px] ${className ?? ""}`}>
      <div className="mb-1 font-mono text-[9px] tracking-[0.14em] text-ink-3">PUBLICIDAD</div>
      <div
        className="flex items-center justify-center overflow-hidden rounded-btn border border-line-1 bg-abyss-1"
        style={{ minHeight: height, maxWidth: "100%" }}
      >
        {inView && provider === "adsterra" && (
          <AdsterraBanner
            scriptSrc={config.adsterraZone || ADSTERRA_NATIVE.scriptSrc}
            containerHash={config.adsterraZone ? config.adsterraZone : ADSTERRA_NATIVE.containerHash}
            width={width}
            height={height}
          />
        )}
        {inView && provider === "propellerads" && config.propellerZone && (
          <PropellerAdsBanner
            zoneId={config.propellerZone}
            width={width}
            height={height}
          />
        )}
      </div>
    </div>
  );
}
