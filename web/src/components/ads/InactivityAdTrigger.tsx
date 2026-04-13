"use client";

import dynamic from "next/dynamic";
import { useInactivityAd } from "@/hooks/useInactivityAd";

const InterstitialOverlay = dynamic(() => import("./InterstitialOverlay"), {
  ssr: false,
});

/**
 * Wrapper that shows an interstitial overlay after user inactivity.
 * Place in pages where passive browsing is expected (homepage, directory).
 * NOT for episode pages (user watching video = "inactive" but engaged).
 */
export default function InactivityAdTrigger() {
  const [show, dismiss] = useInactivityAd();

  if (!show) return null;
  return <InterstitialOverlay onClose={dismiss} />;
}
