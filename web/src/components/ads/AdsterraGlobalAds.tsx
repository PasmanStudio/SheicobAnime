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

    const popunderSrc =
      process.env.NEXT_PUBLIC_ADSTERRA_POPUNDER_SCRIPT ||
      "https://pl29138490.profitablecpmratenetwork.com/3d/1b/5b/3d1b5b04f03946a5ecb8410c8025f660.js";
    const socialBarSrc =
      process.env.NEXT_PUBLIC_ADSTERRA_SOCIALBAR_SCRIPT ||
      "https://pl29138491.profitablecpmratenetwork.com/81/0e/8b/810e8b5ee34d29a8061284101b4768c6.js";

    // Detect touch/mobile — popunder opens a new window on every click,
    // which is catastrophic on mobile. Disable it for touch devices entirely.
    const isTouchDevice =
      window.matchMedia("(pointer: coarse)").matches || window.innerWidth < 768;

    // Load Popunder script — DESKTOP ONLY
    if (popunderSrc && !isTouchDevice) {
      const popScript = document.createElement("script");
      popScript.src = popunderSrc;
      popScript.async = true;
      popScript.dataset.cfasync = "false";
      document.body.appendChild(popScript);
    }

    // Load Social Bar script (notification-style overlay — safe for mobile)
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
