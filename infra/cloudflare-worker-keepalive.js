/**
 * Cloudflare Worker — Keep-alive ping for Railway free tier.
 * Cron trigger: every 25 minutes → GET /health on the API.
 *
 * Deploy:
 *   1. Go to Cloudflare Dashboard → Workers & Pages → Create Worker
 *   2. Paste this script
 *   3. Add Trigger → Cron: "*/25 * * * *"
 *   4. Set environment variable: API_HEALTH_URL = https://<railway-url>/health
 */

export default {
  async scheduled(event, env, ctx) {
    const url = env.API_HEALTH_URL;
    if (!url) {
      console.error("API_HEALTH_URL env var not configured");
      return;
    }

    try {
      const response = await fetch(url, {
        method: "GET",
        headers: { "User-Agent": "SheicobAnime-KeepAlive/1.0" },
      });

      const body = await response.text();
      console.log(`Keep-alive: ${response.status} — ${body}`);

      if (!response.ok) {
        console.error(`Health check failed: HTTP ${response.status}`);
      }
    } catch (err) {
      console.error(`Keep-alive fetch failed: ${err.message}`);
    }
  },

  // Also respond to manual HTTP requests for testing
  async fetch(request, env) {
    return new Response(
      JSON.stringify({ worker: "keepalive", target: env.API_HEALTH_URL || "not configured" }),
      { headers: { "Content-Type": "application/json" } }
    );
  },
};
