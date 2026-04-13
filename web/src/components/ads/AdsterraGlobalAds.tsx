"use client";

import { useEffect, useRef } from "react";
import { getAdProvider } from "@/lib/ad-config";
import { hasAdConsent } from "./ConsentBanner";

/**
 * Loads global Adsterra scripts (Popunder + Social Bar).
 * These are NOT per-placement — they fire once per page session.
 * Must be placed in the root layout, outside of any conditional.
 */
export default function AdsterraGlobalAds() {
  const loaded = useRef(false);

  useEffect(() => {
    if (loaded.current) return;

    const provider = getAdProvider();
    if (provider !== "adsterra") return;
    if (!hasAdConsent()) return;

    const popunderSrc = process.env.NEXT_PUBLIC_ADSTERRA_POPUNDER_SCRIPT;
    const socialBarSrc = process.env.NEXT_PUBLIC_ADSTERRA_SOCIALBAR_SCRIPT;

    // Load Popunder script (fires once per session)
    if (popunderSrc) {
      const popScript = document.createElement("script");
      popScript.src = popunderSrc;
      popScript.async = true;
      popScript.dataset.cfasync = "false";
      document.body.appendChild(popScript);
    }

    // Load Social Bar script (notification-style overlay)
    if (socialBarSrc) {
      const sbScript = document.createElement("script");
      sbScript.src = socialBarSrc;
      sbScript.async = true;
      sbScript.dataset.cfasync = "false";
      document.body.appendChild(sbScript);
    }

    loaded.current = true;
  }, []);

  return null;
}
