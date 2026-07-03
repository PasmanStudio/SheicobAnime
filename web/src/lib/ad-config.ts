/**
 * AD_CONFIG — ONLY place in the codebase with ad zone/unit IDs.
 * All ad placements MUST use AdSlot with a key from this config.
 *
 * Supported providers:
 * - adsterra: Native banners (recommended for streaming)
 * - propellerads: Push + native ads
 * - stub: Development mode (no real ads)
 *
 * Auditoría jun-2026 (doc "Estrategia de ads y monetización"):
 * 21 → 8 placements activos. Los demás quedan `enabled: false` —
 * el markup puede seguir existiendo, AdSlot no renderiza nada.
 * Regla de oro: nunca un ad entre el player y sus controles.
 */

export type AdProvider = "adsterra" | "propellerads" | "stub";

export type AdPlacement =
  | "home_top"
  | "home_mid"
  | "home_bottom"
  | "series_top"
  | "series_sidebar"
  | "series_below_info"
  | "series_bottom"
  | "episode_top"
  | "episode_above_player"
  | "episode_mid"
  | "episode_below_player"
  | "episode_bottom"
  | "search_top"
  | "search_bottom"
  | "directory_top"
  | "directory_bottom"
  | "genres_top"
  | "genre_top"
  | "genre_bottom"
  | "profile_top"
  | "profile_bottom";

export interface AdPlacementConfig {
  /** Si está apagado, AdSlot renderiza null (la poda es config, no código) */
  enabled: boolean;
  /** Adsterra native banner zone ID (legacy — not used with real Adsterra) */
  adsterraZone?: string;
  /** PropellerAds zone ID */
  propellerZone?: string;
  /** Dimensions for the placement — desktop */
  width: number;
  height: number;
  /** Dimensions en móvil (<768px). 728×90 no existe en móvil. */
  mobileWidth: number;
  mobileHeight: number;
  /** Description for debugging */
  description: string;
}

/**
 * Adsterra native banner config — shared across all placements.
 * Script src and container hash come from Adsterra's embed code.
 */
export const ADSTERRA_NATIVE = {
  scriptSrc:
    process.env.NEXT_PUBLIC_ADSTERRA_NATIVE_SCRIPT ||
    "https://pl30177589.effectivecpmnetwork.com/fff9b8d7f1888f68ae69ce1f0634afaf/invoke.js",
  containerHash:
    process.env.NEXT_PUBLIC_ADSTERRA_NATIVE_HASH ||
    "fff9b8d7f1888f68ae69ce1f0634afaf",
} as const;

/** Formato móvil estándar: 320×100 en flujo. */
const MOBILE_BANNER = { mobileWidth: 320, mobileHeight: 100 } as const;

/**
 * AD_CONFIG — placement-to-zone mapping.
 * Zone IDs are set via environment variables to keep them out of source.
 */
export const AD_CONFIG: Record<AdPlacement, AdPlacementConfig> = {
  // ── ELIMINADO: arriba del hero compite con la propia marca ──
  home_top: {
    enabled: false,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_HOME_TOP ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_HOME_TOP ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "ELIMINADO — banner above hero on homepage",
  },
  // ── MANTENER: entre "Últimos episodios" y "Top 10" ──
  home_mid: {
    enabled: true,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_HOME_MID ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_HOME_MID ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "Banner between homepage sections",
  },
  // ── MANTENER: cierre de página ──
  home_bottom: {
    enabled: true,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_HOME_BOTTOM ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_HOME_BOTTOM ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "Banner at bottom of homepage",
  },
  // ── ELIMINADOS: 3 slots extra en una página = CPM deprimido ──
  series_top: {
    enabled: false,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_SERIES_TOP ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_SERIES_TOP ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "ELIMINADO — banner at top of series page",
  },
  series_sidebar: {
    enabled: false,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_SERIES_SIDEBAR ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_SERIES_SIDEBAR ?? "",
    width: 300,
    height: 250,
    mobileWidth: 300,
    mobileHeight: 250,
    description: "ELIMINADO — sidebar ad on series page (muere en móvil)",
  },
  // ── MANTENER: después del bloque de info ──
  series_below_info: {
    enabled: true,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_SERIES_BELOW ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_SERIES_BELOW ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "Banner below series info section",
  },
  series_bottom: {
    enabled: false,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_SERIES_BOTTOM ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_SERIES_BOTTOM ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "ELIMINADO — banner at bottom of series page",
  },
  // ── ELIMINADOS: cada píxel arriba del player cuesta retención ──
  episode_top: {
    enabled: false,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_EP_TOP ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_EP_TOP ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "ELIMINADO — banner at top of episode page",
  },
  episode_above_player: {
    enabled: false,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_EP_ABOVE ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_EP_ABOVE ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "ELIMINADO — empuja el player bajo el fold",
  },
  episode_mid: {
    enabled: false,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_EP_MID ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_EP_MID ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "ELIMINADO — consolidado en episode_bottom",
  },
  // ── MANTENER: el mejor slot. Post-visionado, 100% viewability ──
  episode_below_player: {
    enabled: true,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_EP_BELOW ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_EP_BELOW ?? "",
    width: 728,
    height: 90,
    // Acá va el 300×250 en móvil (mejor CPM del sitio)
    mobileWidth: 300,
    mobileHeight: 250,
    description: "Banner below video player — el mejor slot del sitio",
  },
  // ── MANTENER: uno solo, entre comentarios y footer ──
  episode_bottom: {
    enabled: true,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_EP_BOTTOM ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_EP_BOTTOM ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "Banner at bottom of episode page (post-comentarios)",
  },
  // ── Catálogo: solo *_bottom — el usuario buscando no debe encontrar un ad primero ──
  search_top: {
    enabled: false,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_SEARCH_TOP ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_SEARCH_TOP ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "ELIMINADO — banner at top of search results",
  },
  search_bottom: {
    enabled: true,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_SEARCH_BOTTOM ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_SEARCH_BOTTOM ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "Banner at bottom of search results",
  },
  directory_top: {
    enabled: false,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_DIR_TOP ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_DIR_TOP ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "ELIMINADO — banner at top of directory page",
  },
  directory_bottom: {
    enabled: true,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_DIR_BOTTOM ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_DIR_BOTTOM ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "Banner at bottom of directory page",
  },
  genres_top: {
    enabled: false,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_GENRES_TOP ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_GENRES_TOP ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "ELIMINADO — banner at top of genres listing page",
  },
  genre_top: {
    enabled: false,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_GENRE_TOP ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_GENRE_TOP ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "ELIMINADO — banner at top of individual genre page",
  },
  genre_bottom: {
    enabled: true,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_GENRE_BOTTOM ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_GENRE_BOTTOM ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "Banner at bottom of individual genre page",
  },
  // ── ELIMINADO: el perfil es el producto de engagement — sin peaje ──
  profile_top: {
    enabled: false,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_PROFILE_TOP ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_PROFILE_TOP ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "ELIMINADO — banner at top of profile/lists pages",
  },
  profile_bottom: {
    enabled: true,
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_PROFILE_BOTTOM ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_PROFILE_BOTTOM ?? "",
    width: 728,
    height: 90,
    ...MOBILE_BANNER,
    description: "Banner at bottom of user profile, lists, tier lists, history and watchlist pages",
  },
};

/**
 * Get current ad provider from environment.
 * Defaults to 'stub' for development safety.
 */
export function getAdProvider(): AdProvider {
  const provider = (process.env.NEXT_PUBLIC_AD_PROVIDER ?? "").trim() || "adsterra";
  if (provider === "adsterra" || provider === "propellerads") {
    return provider;
  }
  return "stub";
}

/**
 * Check if a placement has a valid zone ID for the current provider.
 */
export function hasValidZone(placement: AdPlacement): boolean {
  const config = AD_CONFIG[placement];
  const provider = getAdProvider();

  if (!config.enabled) return false;
  if (provider === "stub") return false;
  if (provider === "adsterra") {
    // Check per-placement zone OR shared native banner config
    return Boolean(config.adsterraZone) || Boolean(ADSTERRA_NATIVE.scriptSrc);
  }
  if (provider === "propellerads") return Boolean(config.propellerZone);

  return false;
}
