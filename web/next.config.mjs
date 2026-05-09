import { withSentryConfig } from "@sentry/nextjs";

/** @type {import('next').NextConfig} */
const nextConfig = {
  images: {
    // Serve cover images directly from the source CDN (cdn.jkdesa.com, etc.)
    // instead of proxying through Vercel's image optimization pipeline.
    // This eliminates the Vercel → CDN → Vercel double-hop that adds 400-800ms
    // of latency per image. WebP conversion is skipped but covers are already
    // compressed JPEGs so the tradeoff is worth it.
    unoptimized: true,
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
