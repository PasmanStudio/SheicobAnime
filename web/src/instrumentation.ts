export async function register() {
  // Sentry's Node SDK is not supported on Cloudflare Workers — skipped via DISABLE_SENTRY
  if (process.env.NEXT_RUNTIME === "nodejs" && !process.env.DISABLE_SENTRY) {
    await import("../sentry.server.config");
  }

  if (process.env.NEXT_RUNTIME === "edge") {
    await import("../sentry.edge.config");
  }
}
