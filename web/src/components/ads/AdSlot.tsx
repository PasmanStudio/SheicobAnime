"use client";

import { useState, useEffect } from "react";
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
  loading: () => <AdSkeleton />,
});

const PropellerAdsBanner = dynamic(() => import("./PropellerAdsBanner"), {
  ssr: false,
  loading: () => <AdSkeleton />,
});

export interface AdSlotProps {
  /** Placement key from AD_CONFIG */
  placement: AdPlacement;
  /** Optional className for the wrapper */
  className?: string;
}

/**
 * Skeleton placeholder to prevent CLS while ad loads.
 */
function AdSkeleton() {
  return (
    <div
      className="bg-zinc-800/50 animate-pulse rounded"
      style={{ minHeight: 90, minWidth: 728, maxWidth: "100%" }}
      aria-hidden="true"
    />
  );
}

/**
 * AdSlot — ONLY ad component to use in pages.
 * Uses AD_CONFIG for zone IDs and AD_PROVIDER for network selection.
 *
 * Features:
 * - Multi-provider support (Adsterra, PropellerAds, stub)
 * - Lazy loading to prevent CLS
 * - Consent-aware (no ads without GDPR consent)
 * - Server renders nothing (prevents hydration mismatch)
 *
 * Contract: AD_CONFIG is the ONLY place with ad zone IDs.
 */
export default function AdSlot({ placement, className }: AdSlotProps) {
  const [mounted, setMounted] = useState(false);
  const [hasConsent, setHasConsent] = useState(false);
  const [isMobile, setIsMobile] = useState(false);

  useEffect(() => {
    setMounted(true);
    setHasConsent(hasAdConsent());
    // Adsterra banner scripts (invoke.js) attach global click interceptors that
    // open popup windows on every tap — disable all banner ads on touch devices.
    setIsMobile(
      window.matchMedia("(pointer: coarse)").matches || window.innerWidth < 768,
    );
  }, []);

  // SSR: render nothing to avoid hydration issues
  if (!mounted) {
    return null;
  }

  // Mobile: skip all ad scripts to prevent popup hijacking on tap
  if (isMobile) {
    return null;
  }

  const provider = getAdProvider();
  const config = AD_CONFIG[placement];

  // Stub mode: show placeholder in development
  if (provider === "stub") {
    return (
      <div
        className={`flex items-center justify-center bg-zinc-800/30 border border-dashed border-zinc-600 rounded text-zinc-500 text-xs ${className ?? ""}`}
        style={{
          minWidth: config.width,
          minHeight: config.height,
          maxWidth: "100%",
        }}
      >
        Ad: {placement}
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

  // Render provider-specific banner
  return (
    <div className={`flex justify-center ${className ?? ""}`}>
      {provider === "adsterra" && (
        <AdsterraBanner
          scriptSrc={config.adsterraZone || ADSTERRA_NATIVE.scriptSrc}
          containerHash={config.adsterraZone ? config.adsterraZone : ADSTERRA_NATIVE.containerHash}
          width={config.width}
          height={config.height}
        />
      )}
      {provider === "propellerads" && config.propellerZone && (
        <PropellerAdsBanner
          zoneId={config.propellerZone}
          width={config.width}
          height={config.height}
        />
      )}
    </div>
  );
}
