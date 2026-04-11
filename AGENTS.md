# AGENTS.md — Guidance for AI Agents

This file tells AI agents (GitHub Copilot coding agent, Claude Code, etc.)
how to work effectively in this repository.

## Repository purpose
SheicobAnime is an anime streaming index platform built with .NET 8 + Next.js 14.
It indexes publicly embeddable video mirrors. It NEVER hosts video directly.

## Before making any change

### 1. Check which layer you're touching
- `/api/**` → use backend-engineer chatmode context
- `/web/**` → use frontend-engineer chatmode context
- `/scraper/**` → use scraper-engineer chatmode context
- `/infra/**` → use infra-engineer chatmode context

### 2. Verify the 4 scaling contracts are intact
These must NEVER be broken:
```
api/AnimeIndex.Api/Infrastructure/Cache/ICacheService.cs
api/AnimeIndex.Api/Infrastructure/Scraping/IScrapeStrategy.cs
web/src/components/ads/AdSlot.tsx  (AD_CONFIG is the only ad unit config)
web/src/components/comments/CommentSection.tsx  (COMMENT_PROVIDER switch)
```

### 3. Verify legal constraints
```
MirrorProbeService.IsEmbeddableAsync() MUST be called for every new mirror URL
blocked_slugs table MUST be checked before every scrape operation
No code path may store video files, video streams, or binary media
```

## How to run and verify changes

### API
```bash
cd api
dotnet restore AnimeIndex.sln
dotnet build AnimeIndex.sln
dotnet test AnimeIndex.sln
dotnet run --project AnimeIndex.Api
# Verify: curl http://localhost:5000/health
```

### Frontend
```bash
cd web
npm install
npm run type-check
npm run build
# Verify: npm run dev and check http://localhost:3000
```

### Database
```bash
# Local only — never run migrations against production in CI incorrectly
cd api
dotnet ef database update --project AnimeIndex.Api
```

## Pull request requirements

Every PR must pass:
1. `dotnet test` — all tests green
2. `npm run type-check` — zero TypeScript errors
3. `npm run build` — Next.js builds without errors
4. No architectural contract violations (see security-reviewer chatmode)
5. No secrets introduced in code

## File naming conventions

### C# (API)
- Endpoints: `{Feature}Endpoints.cs`
- DTOs: `{Feature}Dto.cs`
- Validators: `{Feature}Validator.cs`
- Scraper strategies: `Source{N}Strategy.cs`

### TypeScript (Frontend)
- Pages: `page.tsx` (Next.js convention)
- Components: `PascalCase.tsx`
- Hooks: `use{HookName}.ts`
- Utilities: `camelCase.ts`

## What agents MUST NOT do in this repo

- Commit `.env` files with real values
- Add `any` types in TypeScript
- Call Redis directly in API code (use ICacheService)
- Call scraper strategies directly in jobs (use IScrapeStrategy)
- Add inline ad scripts outside AdSlot.tsx
- Add inline comment scripts outside CommentSection.tsx
- Add `<iframe>` elements in Server Components
- Create INSERT statements without ON CONFLICT handling in scraper code
- Store video files, video streams, or binary media content
- Remove or modify the core table structure without a migration file
