-- =============================================================
-- SheicobAnime — Row-Level Security policies
-- Run this once in the Supabase SQL editor (or via psql).
--
-- Architecture note:
--   The API connects with the service_role / postgres user which
--   bypasses RLS entirely — no policies are needed for the API.
--   The anon key is NEVER used from the frontend (all data goes
--   through the Next.js → Railway API layer).
--   Therefore: enable RLS on every table, add NO permissive
--   policies for anon/authenticated, and deny all direct access.
-- =============================================================

-- ── Catalog tables (read-only public data) ──────────────────
-- We still don't expose these via anon because the API is the
-- single authorized access point. If you later want a Supabase
-- JS client to read catalog data directly, add SELECT policies.

ALTER TABLE public.series          ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.genres          ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.episodes        ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.mirrors         ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.series_genres   ENABLE ROW LEVEL SECURITY;

-- ── Internal / scraper tables (no public access ever) ───────
ALTER TABLE public.blocked_slugs   ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.scrape_jobs     ENABLE ROW LEVEL SECURITY;

-- ── User data table (device-based, no auth token) ───────────
ALTER TABLE public.watch_progress  ENABLE ROW LEVEL SECURITY;

-- =============================================================
-- All tables now have RLS enabled with a default-deny posture.
-- The API service_role connection bypasses these policies.
-- No anon or authenticated policies are intentionally defined.
-- =============================================================
