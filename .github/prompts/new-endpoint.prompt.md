# Prompt: Implement new API endpoint
# Usage: /new-endpoint in Copilot chat

Create a new Minimal API endpoint for SheicobAnime following all architectural contracts.

Before writing code:
1. Check existing feature folders in `/api/AnimeIndex.Api/Features/` for patterns
2. Verify the response DTO type exists in `types.ts` in the frontend
3. Confirm the cache key pattern follows `entity:id[:subresource][:page]`

The endpoint must:
- Use `ICacheService` for reads (get cache → return if hit → query DB → set cache → return)
- Return `{ data, total, page, pageSize }` for lists or a single DTO for single resources
- Return `{ error: string, code: string }` on failure — never expose internals
- Apply `.WithRateLimiting("public")` unless it's an admin endpoint
- Have a corresponding FluentValidation validator for all query parameters
- Have at least one integration test in `AnimeIndex.Api.Tests/Integration/`

After writing code, verify:
- The DTO shape matches the TypeScript type in `/web/src/lib/types.ts`
- The cache is invalidated in the relevant scraper upsert method
- The endpoint is registered in the correct feature endpoint registration method
