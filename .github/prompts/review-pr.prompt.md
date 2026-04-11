# Prompt: Review pull request
# Usage: /review-pr in Copilot chat

Review the current pull request against the SheicobAnime security and architecture checklist.

## Run this review in order:

### 1. Architecture contracts (BLOCKING — PR cannot merge if any fail)
- [ ] No direct Redis calls — only ICacheService
- [ ] No concrete scraper types in Hangfire jobs — only IScrapeStrategy
- [ ] No ad scripts outside AdSlot.tsx
- [ ] No comment scripts outside CommentSection.tsx
- [ ] No raw fetch() in React components — only lib/api.ts
- [ ] No <iframe> in Server Components
- [ ] All scraper inserts use ON CONFLICT DO UPDATE
- [ ] blocked_slugs checked before any scraping
- [ ] MirrorProbeService called for all new mirror URLs

### 2. Security (BLOCKING if any fail)
- [ ] No secrets in code
- [ ] No SQL string interpolation (parameterized only)
- [ ] New endpoints have FluentValidation validators
- [ ] iframes have sandbox attribute
- [ ] Playwright URLs validated (https only, no javascript: scheme)

### 3. Performance (BLOCKING for pages)
- [ ] Ad slots have min-height to prevent CLS
- [ ] Heavy components (comments, player) use IntersectionObserver
- [ ] New pages have correct rendering strategy (SSR/ISR/CSR)
- [ ] New DB queries have appropriate indexes

### 4. Code quality (warnings, not blocking)
- [ ] No `any` TypeScript type
- [ ] DTOs use record types in C#
- [ ] Async methods use CancellationToken
- [ ] New features have at least one test

### Output format
For each blocking issue: **BLOCKING: [description] in [file:line]**
For each warning: **WARNING: [description] in [file:line]**
End with: **VERDICT: APPROVE / REQUEST CHANGES**
