import * as Sentry from "@sentry/nextjs";

Sentry.init({
  dsn: process.env.NEXT_PUBLIC_SENTRY_DSN,
  enabled: !!process.env.NEXT_PUBLIC_SENTRY_DSN,

  tracesSampleRate: 0.1, // 10% — free tier friendly
  replaysSessionSampleRate: 0,
  replaysOnErrorSampleRate: 0,

  environment: process.env.NODE_ENV,
  release: process.env.NEXT_PUBLIC_VERCEL_GIT_COMMIT_SHA,

  initialScope: {
    tags: { service: "web" },
  },

  beforeSend(event) {
    // Strip PII: remove user IP
    if (event.user) {
      delete event.user.ip_address;
    }
    return event;
  },
});
