import { withSentryConfig } from "@sentry/nextjs";

/** @type {import('next').NextConfig} */
const nextConfig = {
  images: {
    // Allow images from JKAnime CDN domains and Supabase storage.
    // Next.js will auto-convert to WebP and cache on Vercel's edge CDN,
    // eliminating repeated fetches to the origin CDN.
    remotePatterns: [
      { protocol: "https", hostname: "cdn.jkdesa.com" },
      { protocol: "https", hostname: "**.jkdesa.com" },
      { protocol: "https", hostname: "**.jkanime.net" },
      { protocol: "https", hostname: "**.supabase.co" },
      { protocol: "https", hostname: "**.supabase.in" },
      { protocol: "https", hostname: "asset.seekstreaming.info" },
      // Catch-all for any other image origin already in the DB
      { protocol: "https", hostname: "**" },
    ],
  },
};

export default withSentryConfig(nextConfig, {
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
