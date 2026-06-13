import { defineCloudflareConfig } from "@opennextjs/cloudflare";
import kvIncrementalCache from "@opennextjs/cloudflare/overrides/incremental-cache/kv-incremental-cache";
import { withRegionalCache } from "@opennextjs/cloudflare/overrides/incremental-cache/regional-cache";

// Incremental cache (ISR) sobre Cloudflare KV + una capa regional con la Cache
// API (per-colo) delante para no ir a KV en cada hit. Habilita que las páginas
// con `revalidate` se cacheen en el edge: Render solo se consulta en el cache
// miss (cada N segundos), no por request. Es EL fix de velocidad.
//
// Requiere el binding NEXT_INC_CACHE_KV en wrangler.jsonc.
export default defineCloudflareConfig({
  incrementalCache: withRegionalCache(kvIncrementalCache, {
    mode: "long-lived",
  }),
});
