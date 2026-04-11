# Prompt: Create a new Next.js page
# Usage: /new-page in Copilot chat

Create a new Next.js 14 App Router page for SheicobAnime.

## Before writing code, determine the rendering strategy:

| Page type | Strategy | Implementation |
|-----------|----------|---------------|
| Listing (genres, trending) | ISR | `export const revalidate = 3600` |
| Detail with fresh data (series, episode) | SSR | No revalidate, fetch in page component |
| User-interactive only (search) | SSR per request | No revalidate + URL params |
| Player/comments | Client-side | `'use client'` + useEffect |

## Every page MUST have:
1. `generateMetadata()` function with og:title, og:description, og:image
2. Correct rendering strategy for its content type
3. A loading.tsx with skeleton UI in the same folder
4. An error.tsx for error boundaries
5. All API calls via `src/lib/api.ts` — never raw fetch

## SEO requirements for episode pages:
```typescript
// JSON-LD VideoObject schema — required for episode pages
export default async function EpisodePage({ params }) {
  const episode = await getEpisode(params.slug, params.episode);

  const jsonLd = {
    '@context': 'https://schema.org',
    '@type': 'VideoObject',
    name: `${episode.series.title} Episode ${episode.episodeNumber}`,
    description: episode.series.synopsis,
    thumbnailUrl: episode.thumbnailUrl,
    uploadDate: episode.airedAt,
  };

  return (
    <>
      <script
        type="application/ld+json"
        dangerouslySetInnerHTML={{ __html: JSON.stringify(jsonLd) }}
      />
      {/* Page content */}
    </>
  );
}
```

## Player placement rule (CRITICAL):
The EpisodePlayer MUST be in a separate 'use client' component.
The parent page.tsx MUST be a Server Component.
The iframe MUST NEVER appear in the SSR HTML output.

## Ad placement:
- Series page: `<AdSlot placement="above-fold" />` below hero
- Episode page: `<AdSlot placement="below-player" />` and `<AdSlot placement="below-episodes" />`
- Never place ads on homepage, search, or genre listing pages
