"use client";

import { useState, useEffect, useCallback, useRef } from "react";
import {
  canShowInterstitial,
  wasInactivityShownThisSession,
  markInactivityShown,
  markInterstitialShown,
} from "@/lib/ad-frequency";

const INACTIVITY_TIMEOUT_MS = 15_000; // 15 seconds
const ACTIVITY_EVENTS = ["mousemove", "scroll", "keypress", "touchstart", "click"] as const;

/**
 * Hook that triggers an interstitial after user inactivity.
 * Only fires on homepage/directory, respects shared frequency cap,
 * max 1 per session, skips when tab is hidden.
 *
 * Returns [shouldShow, dismiss].
 */
export function useInactivityAd(): [boolean, () => void] {
  const [show, setShow] = useState(false);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const firedRef = useRef(false);

  const resetTimer = useCallback(() => {
    if (firedRef.current) return;
    if (timerRef.current) clearTimeout(timerRef.current);

    timerRef.current = setTimeout(() => {
      // Don't fire if tab is hidden
      if (document.hidden) return;
      // Check shared frequency cap + session limit
      if (!canShowInterstitial() || wasInactivityShownThisSession()) return;

      firedRef.current = true;
      markInactivityShown();
      markInterstitialShown();
      setShow(true);
    }, INACTIVITY_TIMEOUT_MS);
  }, []);

  useEffect(() => {
    // Don't even start if already shown this session or can't show
    if (wasInactivityShownThisSession() || !canShowInterstitial()) return;

    // Start initial timer
    resetTimer();

    // Reset on any user activity
    for (const event of ACTIVITY_EVENTS) {
      document.addEventListener(event, resetTimer, { passive: true });
    }

    // Pause when tab hidden, resume when visible
    const handleVisibility = () => {
      if (document.hidden) {
        if (timerRef.current) clearTimeout(timerRef.current);
      } else {
        resetTimer();
      }
    };
    document.addEventListener("visibilitychange", handleVisibility);

    return () => {
      if (timerRef.current) clearTimeout(timerRef.current);
      for (const event of ACTIVITY_EVENTS) {
        document.removeEventListener(event, resetTimer);
      }
      document.removeEventListener("visibilitychange", handleVisibility);
    };
  }, [resetTimer]);

  const dismiss = useCallback(() => setShow(false), []);
  return [show, dismiss];
}
