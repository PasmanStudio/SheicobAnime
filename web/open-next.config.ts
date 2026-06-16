import { defineCloudflareConfig } from "@opennextjs/cloudflare";
import kvIncrementalCache from "@opennextjs/cloudflare/overrides/incremental-cache/kv-incremental-cache";
import { withRegionalCache } from "@opennextjs/cloudflare/overrides/incremental-cache/regional-cache";
import kvNextTagCache from "@opennextjs/cloudflare/overrides/tag-cache/kv-next-tag-cache";

// Incremental cache (ISR) sobre Cloudflare KV + una capa regional con la Cache
// API (per-colo) delante para no ir a KV en cada hit. Habilita que las páginas
// con `revalidate` se cacheen en el edge: Render solo se consulta en el cache
// miss (cada N segundos), no por request. Es EL fix de velocidad.
//
// Requiere el binding NEXT_INC_CACHE_KV en wrangler.jsonc.
//
// tagCache (KV): habilita revalidación ON-DEMAND. Cuando el scraper publica
// contenido nuevo pega a /api/revalidate, que llama revalidateTag("content") →
// purga al instante las páginas con ese tag (ver lib/api.ts CONTENT_CACHE). Usa
// el MISMO KV vía el binding NEXT_TAG_CACHE_KV (no hace falta D1). Sin ese
// binding, revalidateTag es no-op y el contenido se refresca por el TTL de 60s.
export default defineCloudflareConfig({
  incrementalCache: withRegionalCache(kvIncrementalCache, {
    mode: "long-lived",
  }),
  tagCache: kvNextTagCache,
});
