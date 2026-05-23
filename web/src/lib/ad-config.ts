/**
 * AD_CONFIG — ONLY place in the codebase with ad zone/unit IDs.
 * All ad placements MUST use AdSlot with a key from this config.
 *
 * Supported providers:
 * - adsterra: Native banners (recommended for streaming)
 * - propellerads: Push + native ads
 * - stub: Development mode (no real ads)
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
  /** Adsterra native banner zone ID (legacy — not used with real Adsterra) */
  adsterraZone?: string;
  /** PropellerAds zone ID */
  propellerZone?: string;
  /** Dimensions for the placement */
  width: number;
  height: number;
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
    "https://pl29138492.profitablecpmratenetwork.com/7ee40e5de8cc821cc6ce096252393fd4/invoke.js",
  containerHash:
    process.env.NEXT_PUBLIC_ADSTERRA_NATIVE_HASH ||
    "7ee40e5de8cc821cc6ce096252393fd4",
} as const;

/**
 * AD_CONFIG — placement-to-zone mapping.
 * Zone IDs are set via environment variables to keep them out of source.
 * These are fallback/example values that should be overridden.
 */
export const AD_CONFIG: Record<AdPlacement, AdPlacementConfig> = {
  home_top: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_HOME_TOP ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_HOME_TOP ?? "",
    width: 728,
    height: 90,
    description: "Banner above recent section on homepage",
  },
  home_mid: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_HOME_MID ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_HOME_MID ?? "",
    width: 728,
    height: 90,
    description: "Banner between homepage sections",
  },
  home_bottom: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_HOME_BOTTOM ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_HOME_BOTTOM ?? "",
    width: 728,
    height: 90,
    description: "Banner at bottom of homepage",
  },
  series_top: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_SERIES_TOP ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_SERIES_TOP ?? "",
    width: 728,
    height: 90,
    description: "Banner at top of series page",
  },
  series_sidebar: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_SERIES_SIDEBAR ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_SERIES_SIDEBAR ?? "",
    width: 300,
    height: 250,
    description: "Sidebar ad on series page",
  },
  series_below_info: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_SERIES_BELOW ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_SERIES_BELOW ?? "",
    width: 728,
    height: 90,
    description: "Banner below series info section",
  },
  series_bottom: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_SERIES_BOTTOM ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_SERIES_BOTTOM ?? "",
    width: 728,
    height: 90,
    description: "Banner at bottom of series page",
  },
  episode_top: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_EP_TOP ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_EP_TOP ?? "",
    width: 728,
    height: 90,
    description: "Banner at top of episode page",
  },
  episode_above_player: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_EP_ABOVE ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_EP_ABOVE ?? "",
    width: 728,
    height: 90,
    description: "Banner above video player (NOT in player area)",
  },
  episode_mid: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_EP_MID ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_EP_MID ?? "",
    width: 728,
    height: 90,
    description: "Banner in middle of episode page",
  },
  episode_below_player: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_EP_BELOW ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_EP_BELOW ?? "",
    width: 728,
    height: 90,
    description: "Banner below video player",
  },
  episode_bottom: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_EP_BOTTOM ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_EP_BOTTOM ?? "",
    width: 728,
    height: 90,
    description: "Banner at bottom of episode page",
  },
  search_top: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_SEARCH_TOP ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_SEARCH_TOP ?? "",
    width: 728,
    height: 90,
    description: "Banner at top of search results",
  },
  search_bottom: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_SEARCH_BOTTOM ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_SEARCH_BOTTOM ?? "",
    width: 728,
    height: 90,
    description: "Banner at bottom of search results",
  },
  directory_top: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_DIR_TOP ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_DIR_TOP ?? "",
    width: 728,
    height: 90,
    description: "Banner at top of directory page",
  },
  directory_bottom: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_DIR_BOTTOM ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_DIR_BOTTOM ?? "",
    width: 728,
    height: 90,
    description: "Banner at bottom of directory page",
  },
  genres_top: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_GENRES_TOP ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_GENRES_TOP ?? "",
    width: 728,
    height: 90,
    description: "Banner at top of genres listing page",
  },
  genre_top: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_GENRE_TOP ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_GENRE_TOP ?? "",
    width: 728,
    height: 90,
    description: "Banner at top of individual genre page",
  },
  genre_bottom: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_GENRE_BOTTOM ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_GENRE_BOTTOM ?? "",
    width: 728,
    height: 90,
    description: "Banner at bottom of individual genre page",
  },
  profile_top: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_PROFILE_TOP ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_PROFILE_TOP ?? "",
    width: 728,
    height: 90,
    description: "Banner at top of user profile, lists, tier lists, history and watchlist pages",
  },
  profile_bottom: {
    adsterraZone: process.env.NEXT_PUBLIC_ADSTERRA_ZONE_PROFILE_BOTTOM ?? "",
    propellerZone: process.env.NEXT_PUBLIC_PROPELLER_ZONE_PROFILE_BOTTOM ?? "",
    width: 728,
    height: 90,
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

  if (provider === "stub") return false;
  if (provider === "adsterra") {
    // Check per-placement zone OR shared native banner config
    return Boolean(config.adsterraZone) || Boolean(ADSTERRA_NATIVE.scriptSrc);
  }
  if (provider === "propellerads") return Boolean(config.propellerZone);

  return false;
}
