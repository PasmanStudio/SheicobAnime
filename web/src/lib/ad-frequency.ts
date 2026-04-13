const INTERSTITIAL_KEY = "sheicob_interstitial_ts";
const INACTIVITY_SESSION_KEY = "sheicob_inactivity_shown";
const FIRST_VISIT_KEY = "sheicob_first_visit";

const DEFAULT_COOLDOWN_MS = 300_000; // 5 minutes

function getCooldownMs(): number {
  const env = process.env.NEXT_PUBLIC_INTERSTITIAL_COOLDOWN_MS;
  if (env) {
    const parsed = Number.parseInt(env, 10);
    if (!Number.isNaN(parsed) && parsed > 0) return parsed;
  }
  return DEFAULT_COOLDOWN_MS;
}

/**
 * Check if enough time has passed since last interstitial.
 * Shared between navigation interstitial and inactivity overlay.
 */
export function canShowInterstitial(): boolean {
  if (globalThis.window === undefined) return false;

  // Don't show on first visit ever
  const firstVisit = localStorage.getItem(FIRST_VISIT_KEY);
  if (!firstVisit) {
    localStorage.setItem(FIRST_VISIT_KEY, Date.now().toString());
    return false;
  }

  const lastShown = localStorage.getItem(INTERSTITIAL_KEY);
  if (!lastShown) return true;

  const elapsed = Date.now() - Number.parseInt(lastShown, 10);
  return elapsed >= getCooldownMs();
}

/**
 * Record that an interstitial was just shown.
 */
export function markInterstitialShown(): void {
  if (globalThis.window === undefined) return;
  localStorage.setItem(INTERSTITIAL_KEY, Date.now().toString());
}

/**
 * Check if inactivity ad was already shown this session.
 */
export function wasInactivityShownThisSession(): boolean {
  if (globalThis.window === undefined) return true;
  return sessionStorage.getItem(INACTIVITY_SESSION_KEY) === "1";
}

/**
 * Mark inactivity ad as shown for this session.
 */
export function markInactivityShown(): void {
  if (globalThis.window === undefined) return;
  sessionStorage.setItem(INACTIVITY_SESSION_KEY, "1");
}
