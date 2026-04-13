"use client";

import dynamic from "next/dynamic";
import { useInterstitial } from "./InterstitialOverlay";

const InterstitialOverlay = dynamic(() => import("./InterstitialOverlay"), {
  ssr: false,
});

/**
 * Wrapper that shows an interstitial on page load (navigation transition).
 * Place in episode pages to trigger on series->episode navigation.
 */
export default function NavigationAdTrigger() {
  const [show, dismiss] = useInterstitial();

  if (!show) return null;
  return <InterstitialOverlay onClose={dismiss} />;
}
