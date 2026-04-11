# Prompt: Debug an issue
# Usage: /debug in Copilot chat

Debug a problem in SheicobAnime using systematic root-cause analysis.

## Phase 1 — Gather information (do this before suggesting fixes)
1. What is the exact error message or unexpected behavior?
2. Which layer is affected? (API / Frontend / Scraper / DB / Infra)
3. Is this happening in local dev, staging, or production?
4. When did it start? (after which commit or deployment?)
5. Is it reproducible? Always / sometimes / once?

## Phase 2 — Check the most common failure points for this stack

### API failures
- Check: Is DATABASE_URL using PgBouncer Session mode? (Hangfire needs Session, not Transaction)
- Check: Is REDIS_URL in Upstash REST format? (not native TCP URL)
- Check: Is Railway service sleeping? (check /health endpoint directly)
- Check: EF Core migration status? (`dotnet ef migrations list`)
- Check: Hangfire job logs at /hangfire dashboard

### Frontend failures
- Check: Is NEXT_PUBLIC_API_URL set correctly in Vercel env vars?
- Check: Is EpisodePlayer wrapped in 'use client'? (iframe must not be in SSR)
- Check: Is the error a hydration mismatch? (Server HTML ≠ client render)
- Check: Is the API responding? (network tab in DevTools)

### Scraper failures
- Check: Is the source site down or has it changed its HTML structure?
- Check: Is the scrape job in dead_letter status? (check scrape_jobs table)
- Check: Is Playwright headless Chromium installed? (`playwright install chromium`)
- Check: Is the embed URL blocked by X-Frame-Options on source? (MirrorProbeService logs)

### Mirror not loading
- Check: Is mirror is_active = true in DB?
- Check: Does embed URL still respond? (HTTP HEAD manually)
- Check: Has X-Frame-Options changed on the embed host?
- Check: Is mirror fallback hook triggering? (check browser console)

## Phase 3 — Fix approach
Only suggest a fix after completing Phase 1 and Phase 2.
Always explain WHY the fix works, not just what to change.
Always verify the fix doesn't break any scaling contracts.
