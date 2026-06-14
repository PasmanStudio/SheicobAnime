const INTERSTITIAL_KEY = "sheicob_interstitial_ts";
const INTERSTITIAL_SESSION_KEY = "sheicob_interstitial_session";
const INACTIVITY_SESSION_KEY = "sheicob_inactivity_shown";
const FIRST_VISIT_KEY = "sheicob_first_visit";
const PAGEVIEW_COUNT_KEY = "sheicob_pageviews";
const POPUNDER_TS_KEY = "sheicob_popunder_ts";

// Tope propio del popunder (además del que configura Adsterra en su panel):
// como máximo inyectamos el script 1 vez cada N horas por dispositivo, así un
// maratón de varios episodios no dispara un pop por página. Default 4 h.
const DEFAULT_POPUNDER_COOLDOWN_MS = 4 * 60 * 60 * 1000;

function getPopunderCooldownMs(): number {
  const env = process.env.NEXT_PUBLIC_POPUNDER_COOLDOWN_MS;
  if (env) {
    const parsed = Number.parseInt(env, 10);
    if (!Number.isNaN(parsed) && parsed > 0) return parsed;
  }
  return DEFAULT_POPUNDER_COOLDOWN_MS;
}

/**
 * ¿Se puede inyectar el popunder ahora? Respeta un cooldown por dispositivo
 * para que el formato de mayor CPM no se vuelva molesto. Tras inyectarlo,
 * llamar markPopunderFired().
 */
export function canFirePopunder(): boolean {
  if (globalThis.window === undefined) return false;
  try {
    const last = localStorage.getItem(POPUNDER_TS_KEY);
    if (!last) return true;
    return Date.now() - Number.parseInt(last, 10) >= getPopunderCooldownMs();
  } catch {
    return true;
  }
}

/** Marca que se inyectó el popunder (arranca el cooldown). */
export function markPopunderFired(): void {
  if (globalThis.window === undefined) return;
  try {
    localStorage.setItem(POPUNDER_TS_KEY, Date.now().toString());
  } catch {
    /* storage unavailable */
  }
}

// Caps doc 2 (jun-2026): máx 1 interstitial por sesión, 1 cada 24 h por usuario,
// nunca en los primeros 3 pageviews, nunca en rutas de episodio.
const DEFAULT_COOLDOWN_MS = 24 * 60 * 60 * 1000; // 24 h
const MIN_PAGEVIEWS = 3;

/** Ruta de visionado: /series/{slug}/{episodeNumber} */
const EPISODE_ROUTE_RE = /^\/series\/[^/]+\/\d+/;

function getCooldownMs(): number {
  const env = process.env.NEXT_PUBLIC_INTERSTITIAL_COOLDOWN_MS;
  if (env) {
    const parsed = Number.parseInt(env, 10);
    if (!Number.isNaN(parsed) && parsed > 0) return parsed;
  }
  return DEFAULT_COOLDOWN_MS;
}

/**
 * Cuenta un pageview de la sesión (para el mínimo de 3 antes de interstitial).
 * Llamar una vez por navegación (PageViewTracker en el layout).
 */
export function recordPageView(): void {
  if (globalThis.window === undefined) return;
  try {
    const count = Number.parseInt(sessionStorage.getItem(PAGEVIEW_COUNT_KEY) ?? "0", 10);
    sessionStorage.setItem(PAGEVIEW_COUNT_KEY, String(count + 1));
  } catch {
    /* storage unavailable */
  }
}

function getPageViews(): number {
  try {
    return Number.parseInt(sessionStorage.getItem(PAGEVIEW_COUNT_KEY) ?? "0", 10);
  } catch {
    return 0;
  }
}

/**
 * Check if the interstitial can be shown right now.
 * Shared between navigation interstitial and inactivity overlay.
 */
export function canShowInterstitial(): boolean {
  if (globalThis.window === undefined) return false;

  // Never show the interstitial overlay on touch/mobile.
  // It's fullscreen and if invoke.js intercepts the close-button tap the
  // user gets trapped with no way out — worse than any banner.
  if (
    window.matchMedia("(pointer: coarse)").matches ||
    window.innerWidth < 768
  ) {
    return false;
  }

  // Nunca en páginas de episodio — el usuario que está por darle play es sagrado
  if (EPISODE_ROUTE_RE.test(window.location.pathname)) return false;

  // Máx 1 por sesión
  if (sessionStorage.getItem(INTERSTITIAL_SESSION_KEY) === "1") return false;

  // Nunca en los primeros pageviews de la sesión
  if (getPageViews() < MIN_PAGEVIEWS) return false;

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
  try {
    sessionStorage.setItem(INTERSTITIAL_SESSION_KEY, "1");
  } catch {
    /* storage unavailable */
  }
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
