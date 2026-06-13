import { fileURLToPath } from "node:url";
import path from "node:path";
import { withSentryConfig } from "@sentry/nextjs";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

// Cloudflare Workers build: the Sentry SDK adds ~4 MiB to the server bundle,
// blowing the 3 MiB Workers free-plan size limit. CLOUDFLARE_BUILD=1 swaps
// @sentry/nextjs for a no-op stub and skips the Sentry webpack wrapper.
const isCloudflareBuild = !!process.env.CLOUDFLARE_BUILD;

/** @type {import('next').NextConfig} */
const nextConfig = {
  // Security headers — antes vivían en vercel.json, que Cloudflare Workers
  // IGNORA (solo Vercel lo lee). Acá los aplica el router de OpenNext en
  // cualquier hosting. Mantener vercel.json en sync si se vuelve a Vercel.
  async headers() {
    return [
      {
        source: "/api/:path*",
        headers: [{ key: "X-Robots-Tag", value: "noindex" }],
      },
      {
        // Todo el sitio salvo /embed (que necesita iframe same-origin)
        source: "/((?!embed).*)",
        headers: [
          { key: "X-Content-Type-Options", value: "nosniff" },
          { key: "X-Frame-Options", value: "DENY" },
          { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
          { key: "Permissions-Policy", value: "camera=(), microphone=(), geolocation=()" },
          { key: "Strict-Transport-Security", value: "max-age=31536000; includeSubDomains" },
          { key: "Content-Security-Policy", value: "upgrade-insecure-requests" },
        ],
      },
      {
        source: "/embed/:path*",
        headers: [
          { key: "X-Content-Type-Options", value: "nosniff" },
          { key: "X-Frame-Options", value: "SAMEORIGIN" },
          { key: "X-Robots-Tag", value: "noindex, nofollow" },
        ],
      },
    ];
  },
  images: {
    // Sin optimización: los posters vienen de CDNs de terceros (cdn.jkdesa.com,
    // etc.) que bloquean el hotlink de proxies externos (wsrv.nl daba 403 → todas
    // las imágenes rotas). Se sirven directo del origen. La optimización real
    // (resize/WebP) hay que hacerla con Cloudflare Images sobre el dominio propio.
    unoptimized: true,
    remotePatterns: [
      { protocol: "https", hostname: "cdn.jkdesa.com" },
      { protocol: "https", hostname: "**.jkdesa.com" },
      { protocol: "https", hostname: "**.jkanime.net" },
      { protocol: "https", hostname: "**.supabase.co" },
      { protocol: "https", hostname: "**.supabase.in" },
      { protocol: "https", hostname: "asset.seekstreaming.info" },
      { protocol: "https", hostname: "**" },
    ],
  },
  ...(isCloudflareBuild && {
    webpack: (config) => {
      config.resolve.alias["@sentry/nextjs"] = path.resolve(
        __dirname,
        "src/lib/sentry-stub.ts"
      );
      return config;
    },
  }),
};

export default isCloudflareBuild
  ? nextConfig
  : withSentryConfig(nextConfig, {
      // Upload source maps to Sentry for readable stack traces
      org: process.env.SENTRY_ORG,
      project: process.env.SENTRY_PROJECT,

      // Only upload source maps during CI builds (when auth token is present)
      silent: !process.env.SENTRY_AUTH_TOKEN,

      // Wipe source maps after upload so they aren't served to the client
      sourcemaps: {
        deleteSourcemapsAfterUpload: true,
      },

      // Disable Sentry telemetry
      telemetry: false,
    });
