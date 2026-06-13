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
    // Loader custom (src/lib/image-loader.ts): redimensiona + WebP vía wsrv.nl.
    // Los posters de terceros llegaban a tamaño completo — el mayor costo móvil.
    // remotePatterns no aplica con loader custom (lo valida solo el optimizer
    // nativo), así que no hace falta listar hosts.
    loader: "custom",
    loaderFile: "./src/lib/image-loader.ts",
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
