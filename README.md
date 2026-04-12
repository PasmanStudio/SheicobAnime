# SheicobAnime

[![API CI](https://github.com/PasmanStudio/SheicobAnime/actions/workflows/api.yml/badge.svg)](https://github.com/PasmanStudio/SheicobAnime/actions/workflows/api.yml)
[![Web CI](https://github.com/PasmanStudio/SheicobAnime/actions/workflows/web.yml/badge.svg)](https://github.com/PasmanStudio/SheicobAnime/actions/workflows/web.yml)
[![Deploy](https://github.com/PasmanStudio/SheicobAnime/actions/workflows/deploy.yml/badge.svg)](https://github.com/PasmanStudio/SheicobAnime/actions/workflows/deploy.yml)

Anime streaming index platform. Indexes publicly embeddable video mirrors — never hosts video directly.

**Stack:** .NET 9 · Next.js 14 · PostgreSQL · Playwright · Hangfire · Redis · Cloudflare
**Budget:** $0/month (free-tier infrastructure)
**Timeline:** 30 days to MVP

---

## Quick start (local dev)

### Prerequisites — install these first

| Tool | Version | Install |
|------|---------|---------|
| .NET SDK | 8.x | [dotnet.microsoft.com](https://dotnet.microsoft.com/download) |
| Node.js | 20 LTS | [nodejs.org](https://nodejs.org) |
| Docker Desktop | Latest | [docker.com](https://docker.com/products/docker-desktop) |
| Git | Latest | [git-scm.com](https://git-scm.com) |
| VS Code | Latest | [code.visualstudio.com](https://code.visualstudio.com) |

### VS Code extensions (install all)
Open VS Code → `Ctrl+Shift+P` → "Extensions: Show Recommended Extensions" → Install All

### 1. Clone and set up environment
```bash
git clone https://github.com/YOUR-USERNAME/sheicobanime.git
cd sheicobanime
cp .env.example .env
# Edit .env and fill in all values
```

### 2. Start local infrastructure
```bash
docker compose -f infra/docker-compose.yml up -d
# PostgreSQL → localhost:5432
# Redis     → localhost:6379
# Adminer   → localhost:8080
```

### 3. Run database migrations
```bash
cd api
dotnet restore AnimeIndex.sln
dotnet ef database update --project AnimeIndex.Api
```

### 4. Install Playwright browsers
```bash
cd api
dotnet tool install --global Microsoft.Playwright.CLI
playwright install chromium
```

### 5. Start the API
```bash
cd api
dotnet run --project AnimeIndex.Api
# API → http://localhost:5000
# Hangfire dashboard → http://localhost:5000/hangfire
```

### 6. Start the frontend
```bash
cd web
npm install
npm run dev
# Frontend → http://localhost:3000
```

### Verify everything works
```bash
curl http://localhost:5000/health
# Expected: {"status":"ok","db":"ok","cache":"ok","version":"1.0.0"}
```

---

## Using GitHub Copilot with this repo

This repo is fully configured for vibe-coding with GitHub Copilot (Claude Opus 4.6).

### Chatmodes — switch based on what you're building

In VS Code Copilot Chat, click the mode dropdown (bottom of chat panel):

| Mode | Use when |
|------|---------|
| `backend-engineer` | Working on `/api` — endpoints, EF Core, Hangfire, cache |
| `frontend-engineer` | Working on `/web` — pages, components, SSR strategy |
| `scraper-engineer` | Working on `/scraper` — Playwright, IScrapeStrategy, jobs |
| `infra-engineer` | Deployments, Railway, Vercel, Cloudflare, CI/CD |
| `security-reviewer` | Reviewing PRs, checking for vulnerabilities |
| `database-admin` | Schema changes, migrations, query optimization |
| `project-manager` | Planning features, creating issues, phase tracking |

### Reusable prompts — type these in chat

| Command | What it does |
|---------|-------------|
| `/new-endpoint` | Creates a new API endpoint with cache, validator, and test |
| `/new-page` | Creates a new Next.js page with correct rendering strategy |
| `/new-source` | Adds a new anime source to the scraper |
| `/review-pr` | Runs security + architecture review on current PR diff |
| `/debug` | Systematic root-cause analysis for any issue |
| `/plan` | Plans today's coding session based on current phase |

### Context persistence with MemPalace

Every Copilot session starts with amnesia. MemPalace fixes this — see `MEMPALACE_SETUP.md`.

---

## Architecture — the non-negotiable rules

### Four scaling contracts (NEVER break these)

These four abstractions are what make the platform scale without rewrites:

1. **`ICacheService`** — only interface the API uses for cache. Never call Redis directly in endpoints.
2. **`IScrapeStrategy`** — only interface Hangfire jobs call. Never use concrete scraper classes in jobs.
3. **`<AdSlot placement="...">`** — the only component with ad network code. All pages use this — never inline ad scripts.
4. **`COMMENT_PROVIDER` env var** — controls which comment system loads. CommentSection.tsx is the only place that cares.

### Legal architecture (critical)

The platform is legally positioned as a search index:
- `MirrorProbeService.IsEmbeddableAsync()` gates ALL embed URLs before storage
- `blocked_slugs` table is checked before any scrape
- No code path stores video files or binary media
- DMCA takedown removes series AND adds to blocked_slugs

---

## Repository structure

```
sheicobanime/
├── api/                    .NET 8 Minimal API
│   ├── AnimeIndex.Api/
│   │   ├── Features/       One folder per domain (Series, Episodes, Mirrors, Genres, Admin)
│   │   └── Infrastructure/ Cache, Database, Scraping, Scheduling
│   └── AnimeIndex.Api.Tests/
├── web/                    Next.js 14 App Router
│   └── src/
│       ├── app/            Pages (SSR/ISR routing strategy documented in frontend-engineer chatmode)
│       ├── components/     player/, series/, ads/, comments/, layout/
│       └── lib/            api.ts (API client), types.ts (shared types)
├── scraper/                Standalone scraper worker (separate Railway service)
├── db/                     SQL reference migrations + seed data
├── infra/                  docker-compose, Railway configs, Cloudflare rules
├── .github/
│   ├── copilot-instructions.md  ← Master context file (loaded in every session)
│   ├── chatmodes/               ← 7 specialized agent personas
│   ├── prompts/                 ← 5 reusable slash commands
│   └── workflows/               ← CI (tests) + Deploy (Railway + Vercel)
└── .vscode/
    ├── settings.json            ← Copilot + formatter + test runner config
    ├── extensions.json          ← All recommended extensions
    └── tasks.json               ← One-click common operations
```

---

## Deployment (free-tier)

See `.github/workflows/deploy.yml` — automated on every push to `main`.

Manual deploy checklist: see Phase 7 in the master spec document.

## GitHub Actions secrets to set

Go to repo Settings → Secrets and variables → Actions:

| Secret | Where to get it |
|--------|----------------|
| `RAILWAY_TOKEN` | railway.app → Account Settings → Tokens |
| `RAILWAY_SERVICE_ID` | railway.app → Project → Service → Settings |
| `RAILWAY_SCRAPER_SERVICE_ID` | Same, scraper service |
| `VERCEL_TOKEN` | vercel.com → Account Settings → Tokens |
| `VERCEL_ORG_ID` | vercel.com → Project Settings |
| `VERCEL_PROJECT_ID` | vercel.com → Project Settings |
| `SUPABASE_DB_URL_DIRECT` | Supabase → Project Settings → Database → Connection string (direct) |

---

## Current phase

**Phase 1: Local development environment** (Day 1–2)

See GitHub Issues for task breakdown. Use the `project-manager` chatmode to plan sessions.
