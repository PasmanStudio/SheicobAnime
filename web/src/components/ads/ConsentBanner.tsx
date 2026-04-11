"use client";

import { useState, useEffect } from "react";

const CONSENT_KEY = "sheicob_ad_consent";
const CONSENT_VERSION = "1"; // Bump when privacy policy changes

export interface ConsentState {
  given: boolean;
  version: string;
  timestamp: number;
}

/**
 * Check if user has given ad consent.
 */
export function hasAdConsent(): boolean {
  if (typeof window === "undefined") return false;

  try {
    const stored = localStorage.getItem(CONSENT_KEY);
    if (!stored) return false;

    const consent: ConsentState = JSON.parse(stored);
    return consent.given && consent.version === CONSENT_VERSION;
  } catch {
    return false;
  }
}

/**
 * Save user's ad consent decision.
 */
export function saveAdConsent(given: boolean): void {
  if (typeof window === "undefined") return;

  const consent: ConsentState = {
    given,
    version: CONSENT_VERSION,
    timestamp: Date.now(),
  };

  localStorage.setItem(CONSENT_KEY, JSON.stringify(consent));
}

/**
 * GDPR Consent Banner for ads.
 * Shows only once until consent is given or declined.
 * Ads will not load until consent is given.
 */
export default function ConsentBanner() {
  const [visible, setVisible] = useState(false);

  useEffect(() => {
    // Only show if no consent recorded yet
    const stored = localStorage.getItem(CONSENT_KEY);
    if (!stored) {
      setVisible(true);
    }
  }, []);

  const handleAccept = () => {
    saveAdConsent(true);
    setVisible(false);
    // Trigger page reload to load ads
    window.location.reload();
  };

  const handleDecline = () => {
    saveAdConsent(false);
    setVisible(false);
  };

  if (!visible) {
    return null;
  }

  return (
    <div className="fixed bottom-0 left-0 right-0 bg-zinc-900 border-t border-zinc-700 p-4 z-50 shadow-lg">
      <div className="container mx-auto flex flex-col sm:flex-row items-center justify-between gap-4">
        <div className="text-sm text-zinc-300">
          <p>
            We use cookies and advertising to keep this site free. By clicking
            &quot;Accept&quot;, you consent to personalized ads.{" "}
            <a
              href="/privacy"
              className="text-purple-400 hover:text-purple-300 underline"
            >
              Privacy Policy
            </a>
          </p>
        </div>
        <div className="flex gap-3">
          <button
            onClick={handleDecline}
            className="px-4 py-2 text-sm text-zinc-400 hover:text-white transition-colors"
          >
            Decline
          </button>
          <button
            onClick={handleAccept}
            className="px-4 py-2 text-sm bg-purple-600 hover:bg-purple-700 text-white rounded-md transition-colors"
          >
            Accept
          </button>
        </div>
      </div>
    </div>
  );
}
