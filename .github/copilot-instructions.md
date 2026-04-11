# SHEICOBANIME — Master Copilot Instructions

## Project identity
You are working on **SheicobAnime**, an anime streaming index platform.
Goal: index publicly embeddable video mirrors. NEVER host video directly.
Status: MVP build — zero-budget, free-tier infrastructure.

## Stack (non-negotiable — never suggest alternatives)
- **Backend:** .NET 8 Minimal API (C#), ASP.NET Core
- **Frontend:** Next.js 14 App Router, TypeScript, Tailwind CSS
- **Database:** PostgreSQL 16 via Supabase (free tier)
- **ORM:** Entity Framework Core + Npgsql
- **Cache:** Redis via Upstash (HTTP REST client, not native TCP)
- **Scraper:** Playwright .NET (headless Chromium)
- **Scheduler:** Hangfire with PostgreSQL backing
- **CDN/WAF:** Cloudflare (free tier)
- **Hosting:** Railway (API), Vercel (frontend)
- **Object storage:** Cloudflare R2
- **CI/CD:** GitHub Actions

## Repository structure
```
/api          → ASP.NET 9 Minimal API
/web          → Next.js 14 App Router
/scraper      → Playwright scraper worker (separate deployable)
/db           → Migrations SQL reference + seeds
/infra        → docker-compose, Railway configs, Cloudflare rules
.github/      → Copilot config (you are here)
```

## Architecture constraints (ALWAYS enforce these)

### The four scaling contracts — NEVER break these interfaces
1. `ICacheService` — only interface the API uses for cache. Never call Redis directly.
2. `IScrapeStrategy` — only interface the scheduler calls. Never instantiate scrapers directly.
3. `<AdSlot placement="...">` — only ad component in any page. AD_CONFIG is the only place with ad unit IDs.
4. `COMMENT_PROVIDER` env var — only switch for comment system. CommentSection handles all providers.

### Legal architecture (CRITICAL)
- The scraper ONLY indexes URLs that pass `MirrorProbeService.IsEmbeddableAsync()`
- IsEmbeddable checks: HTTP 200 + no X-Frame-Options DENY/SAMEORIGIN + no CSP frame-ancestors none/self
- Never generate code that stores video files, binary media, or video streams
- `blocked_slugs` table must be checked before any scrape operation

### Database conventions
- All PKs are UUID (`gen_random_uuid()`)
- All timestamps are TIMESTAMPTZ with DEFAULT now()
- All scraper writes use `ON CONFLICT DO UPDATE` (upsert — never insert-then-check)
- Connection string uses PgBouncer Session mode for Hangfire, Direct for EF migrations

### API conventions
- All public endpoints: rate limited 60 req/min/IP (sliding window)
- All response lists: `{ data: T[], total: int, page: int, pageSize: int }`
- All errors: `{ error: string, code: string, details?: {} }`
- Admin endpoints require `X-Admin-Key` header
- Cache keys pattern: `entity:id[:subresource][:page]`

### Frontend conventions
- `EpisodePlayer` and all player logic MUST be `'use client'` — iframe NEVER in SSR output
- ALL API calls go through `src/lib/api.ts` — zero raw fetch() in components
- ALL ad placements use `<AdSlot placement="...">` — never inline ad scripts
- ALL comment embeds use `<CommentSection>` — never inline Disqus/Remark42
- `generateMetadata()` required on every public route

### C# conventions
- Use Minimal API endpoints, not controllers
- Use Mapster for DTO mapping (not AutoMapper)
- Use FluentValidation for all input validation
- Use Serilog structured JSON logging to stdout
- Use record types for DTOs and domain models
- Null safety: enable nullable in all projects (`<Nullable>enable</Nullable>`)

### TypeScript/Next.js conventions
- Strict TypeScript (`"strict": true`)
- Server Components by default; `'use client'` only when hooks/interactivity needed
- Import paths use `@/` alias for `src/`
- Tailwind only — no CSS modules, no styled-components
- No `any` type — use `unknown` and narrow

## Environment variables reference

### API (Railway)
```
DATABASE_URL         PostgreSQL connection (PgBouncer Session mode)
DATABASE_URL_DIRECT  PostgreSQL direct connection (EF migrations only)
REDIS_URL            Upstash REST URL format: rediss://default:TOKEN@HOST:PORT
ADMIN_API_KEY        Admin endpoint auth key
HANGFIRE_DASHBOARD_PASSWORD
RESEND_API_KEY       Dead-letter email alerts
SENTRY_DSN
CORS_ORIGINS         Comma-separated allowed origins
```

### Frontend (Vercel)
```
NEXT_PUBLIC_API_URL           Production API base URL
NEXT_PUBLIC_ADSENSE_ID        ca-pub-XXXX
NEXT_PUBLIC_DISQUS_SHORTNAME
NEXT_PUBLIC_COMMENT_PROVIDER  disqus | remark42 | native
NEXT_PUBLIC_REMARK42_URL      (Phase 2+)
```

## Current build phase
Phase: **MVP (Phase 1)** — free-tier infrastructure, modular monolith
Target: 30 days to production, $0/month cost

## Key files to always check before editing
- `/api/AnimeIndex.Api/Infrastructure/Cache/ICacheService.cs` — cache contract
- `/api/AnimeIndex.Api/Infrastructure/Scraping/IScrapeStrategy.cs` — scraper contract
- `/web/src/lib/api.ts` — API client contract
- `/web/src/lib/types.ts` — shared TypeScript types
- `/web/src/components/ads/AdSlot.tsx` — ad contract
- `/web/src/components/comments/CommentSection.tsx` — comment contract
