/**
 * Returns the canonical site URL (no trailing slash).
 *
 * Set NEXT_PUBLIC_SITE_URL in your environment to override.
 * If not set, falls back to the Vercel production URL.
 *
 * Use this everywhere instead of hardcoding the domain so that
 * a single env-var change migrates the whole codebase.
 *
 * @example
 *   import { siteUrl } from "@/lib/site-url";
 *   const link = `${siteUrl()}/listas/${id}`;
 */
export function siteUrl(): string {
  return (process.env.NEXT_PUBLIC_SITE_URL ?? "https://sheicobanime.vercel.app").replace(/\/$/, "");
}
