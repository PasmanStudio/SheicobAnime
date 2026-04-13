"use client";

import { useState, useEffect, useCallback } from "react";
import { canShowInterstitial, markInterstitialShown } from "@/lib/ad-frequency";
import AdSlot from "./AdSlot";

const COUNTDOWN_SECONDS = 5;

/**
 * Fullscreen interstitial ad overlay.
 * Shows a countdown timer, then reveals a close button.
 * Frequency-capped via shared localStorage timestamp.
 */
export default function InterstitialOverlay({
  onClose,
}: Readonly<{
  onClose: () => void;
}>) {
  const [secondsLeft, setSecondsLeft] = useState(COUNTDOWN_SECONDS);
  const [canClose, setCanClose] = useState(false);

  useEffect(() => {
    if (secondsLeft <= 0) {
      setCanClose(true);
      return;
    }
    const timer = setTimeout(() => setSecondsLeft((s) => s - 1), 1000);
    return () => clearTimeout(timer);
  }, [secondsLeft]);

  useEffect(() => {
    markInterstitialShown();
  }, []);

  // Close on Escape key
  useEffect(() => {
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" && canClose) onClose();
    };
    document.addEventListener("keydown", handleKey);
    return () => document.removeEventListener("keydown", handleKey);
  }, [canClose, onClose]);

  return (
    <dialog
      open
      className="fixed inset-0 z-50 bg-black/90 flex flex-col items-center justify-center w-full h-full max-w-none max-h-none m-0 p-0 border-none"
      aria-label="Advertisement"
    >
      {/* Close / Countdown */}
      <div className="absolute top-4 right-4">
        {canClose ? (
          <button
            onClick={onClose}
            className="min-w-[44px] min-h-[44px] px-4 py-2 bg-white text-black rounded-md font-semibold text-sm hover:bg-neutral-200 transition-colors focus:outline-none focus:ring-2 focus:ring-white"
            aria-label="Cerrar anuncio"
          >
            Cerrar ✕
          </button>
        ) : (
          <span className="px-4 py-2 bg-neutral-800 text-neutral-300 rounded-md text-sm">
            Cerrar en {secondsLeft}s
          </span>
        )}
      </div>

      {/* Ad content */}
      <div className="flex flex-col items-center gap-4">
        <AdSlot placement="episode_above_player" className="pointer-events-auto" />
        <AdSlot placement="episode_below_player" className="pointer-events-auto" />
      </div>
    </dialog>
  );
}

/**
 * Hook to determine if an interstitial should be shown.
 * Returns [shouldShow, dismiss] — call dismiss() to hide.
 */
export function useInterstitial(): [boolean, () => void] {
  const [show, setShow] = useState(false);

  useEffect(() => {
    if (canShowInterstitial()) {
      setShow(true);
    }
  }, []);

  const dismiss = useCallback(() => setShow(false), []);
  return [show, dismiss];
}
