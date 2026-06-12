/**
 * Returns the canonical site URL (no trailing slash).
 *
 * ÚNICA fuente de verdad del dominio. Para cambiar de dominio:
 *   1. web/.env.production  → NEXT_PUBLIC_SITE_URL (se inlinea en el build)
 *   2. web/wrangler.jsonc   → vars.NEXT_PUBLIC_SITE_URL (runtime del Worker)
 * Nada más — ningún componente debe hardcodear el dominio.
 *
 * @example
 *   import { siteUrl } from "@/lib/site-url";
 *   const link = `${siteUrl()}/listas/${id}`;
 */
export function siteUrl(): string {
  return (
    process.env.NEXT_PUBLIC_SITE_URL ?? "https://sheicobanime.sheicob.workers.dev"
  ).replace(/\/$/, "");
}
