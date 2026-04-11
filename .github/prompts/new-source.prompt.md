# Prompt: Add new scrape source
# Usage: /new-source in Copilot chat

Add a new anime source site to the SheicobAnime scraper.

Before starting:
1. Review `IScrapeStrategy.cs` — you must implement this exact interface
2. Review `PlaywrightScraperBase.cs` — inherit from this, do not create a new base
3. Review `Source1Strategy.cs` — use as a reference implementation

Steps:
1. Create `/api/AnimeIndex.Api/Infrastructure/Scraping/Strategies/Source{N}Strategy.cs`
2. Implement `IScrapeStrategy`:
   - `SourceName`: short identifier like "animesrc" (lowercase, no spaces)
   - `CanHandle(url)`: return true only for URLs matching this source's domain
   - `ScrapeSeriesAsync`: extract title, synopsis, cover_url, year, status, type, genres, episode list
   - `ScrapeEpisodeMirrorsAsync`: extract all embed URLs for a specific episode
3. Every embed URL discovered MUST be passed through `MirrorProbeService.IsEmbeddableAsync()`
4. Check `blocked_slugs` table before returning any series data
5. All writes use upsert (`ON CONFLICT DO UPDATE`)
6. Add jitter delay between page loads: `await JitterDelayAsync()`
7. Register in DI: `builder.Services.AddScoped<IScrapeStrategy, Source{N}Strategy>()`

The strategy MUST:
- Never store video files or binary media
- Never bypass the MirrorProbeService gate
- Be idempotent (running twice produces identical DB state)
- Handle network errors gracefully (catch and return failed ScrapeResult)
- Respect robots.txt (check before scraping)
