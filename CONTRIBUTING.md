# Contributing to SheicobAnime

Thanks for contributing! This document covers conventions and workflow for the project.

## Branch Strategy

- `main` is the production branch — always deployable
- Create feature branches from `main`: `feat/issue-number-short-description`
- All changes go through Pull Requests with at least 1 review

## Commit Convention

We use [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<scope>): <short description>

[optional body]

Closes #<issue-number>
```

### Types

| Type | When to use |
|------|-------------|
| `feat` | New feature or endpoint |
| `fix` | Bug fix |
| `chore` | Build, CI, dependency updates |
| `docs` | Documentation only |
| `refactor` | Code restructuring (no behavior change) |
| `test` | Adding or updating tests |
| `style` | Formatting, missing semicolons (no logic change) |

### Scopes

| Scope | Area |
|-------|------|
| `api` | Backend / .NET API |
| `web` | Frontend / Next.js |
| `scraper` | Playwright scraper |
| `infra` | Docker, CI/CD, deployment |
| `db` | Migrations, schema changes |

### Examples

```
feat(api): add GET /series endpoint with pagination
fix(web): prevent iframe rendering in SSR output
chore(infra): update docker-compose postgres to 16.2
test(api): add integration tests for mirror report endpoint
docs: update README with Phase 2 status

Closes #9
```

## Pull Request Process

1. Create a branch: `git checkout -b feat/9-series-endpoints`
2. Make your changes following the conventions below
3. Run checks locally before pushing:
   ```bash
   # Backend
   cd api && dotnet build AnimeIndex.sln && dotnet test AnimeIndex.sln

   # Frontend
   cd web && npm run type-check && npm run build
   ```
4. Push and create a PR against `main`
5. Fill in the PR template — link the issue with `Closes #N`
6. Request review from at least 1 team member
7. Merge after approval (squash merge preferred)

## Code Conventions

### C# (.NET API)

- Minimal API endpoints, not controllers
- `record` types for DTOs and domain models
- FluentValidation for all input validation
- Mapster for DTO mapping (not AutoMapper)
- Serilog structured JSON logging
- `<Nullable>enable</Nullable>` in all projects
- Cache via `ICacheService` only — never call Redis directly
- Scraper via `IScrapeStrategy` only — never instantiate concrete scrapers

### TypeScript (Next.js)

- Strict mode (`"strict": true`)
- Server Components by default; `'use client'` only for hooks/interactivity
- `@/` import alias for `src/`
- Tailwind only — no CSS modules, no styled-components
- No `any` type — use `unknown` and narrow
- All API calls via `src/lib/api.ts` — zero raw `fetch()` in components
- All ads via `<AdSlot>` — never inline ad scripts
- All comments via `<CommentSection>` — never inline Disqus/Remark42

### Database

- All PKs are UUID with `gen_random_uuid()`
- All timestamps are TIMESTAMPTZ with `DEFAULT now()`
- Scraper writes use `ON CONFLICT DO UPDATE` (upsert)
- Always create a migration file for schema changes

## Environment Variables

- Never commit `.env` with real values
- Keep `.env.example` updated when adding new variables
- Variable naming: `SCREAMING_SNAKE_CASE`
- Frontend public vars: prefix with `NEXT_PUBLIC_`

## Architecture Rules (Never Break)

1. **ICacheService** — only cache interface. Swap implementations in DI only.
2. **IScrapeStrategy** — only scraper interface. Jobs never reference concrete types.
3. **AdSlot** — only ad component. `AD_CONFIG` is the single source of ad unit IDs.
4. **COMMENT_PROVIDER** — only comment switch. `CommentSection` handles all providers.
5. **MirrorProbeService** — every embed URL must pass `IsEmbeddableAsync()` before storage.
6. **blocked_slugs** — checked before every scrape operation.
7. **No video storage** — only metadata and embed URLs. Ever.
