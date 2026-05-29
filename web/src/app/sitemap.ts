import type { MetadataRoute } from "next";
import { getSeries, getGenres, getRecentEpisodes } from "@/lib/api";
import { siteUrl as getSiteUrl } from "@/lib/site-url";

export const dynamic = "force-dynamic";

// Fetch up to this many series per API page. The Next.js fetch cache means
// subsequent sitemap hits re-use cached responses (revalidate: 300 on the API).
const SERIES_PAGE_SIZE = 500;

export default async function sitemap(): Promise<MetadataRoute.Sitemap> {
  const baseUrl = getSiteUrl();

  // ── Static pages ──────────────────────────────────────────────────────────
  const staticPages: MetadataRoute.Sitemap = [
    { url: baseUrl,                          lastModified: new Date(), changeFrequency: "daily",   priority: 1.0 },
    { url: `${baseUrl}/temporada`,           lastModified: new Date(), changeFrequency: "daily",   priority: 0.9 },
    { url: `${baseUrl}/genres`,             lastModified: new Date(), changeFrequency: "weekly",  priority: 0.7 },
    { url: `${baseUrl}/search`,             lastModified: new Date(), changeFrequency: "weekly",  priority: 0.5 },
  ];

  // ── All series pages (paginated) ──────────────────────────────────────────
  let seriesPages: MetadataRoute.Sitemap = [];
  try {
    let page = 1;
    let total = Infinity;
    while (seriesPages.length < total) {
      const result = await getSeries({ page, pageSize: SERIES_PAGE_SIZE });
      total = result.total;
      seriesPages.push(
        ...result.data.map((s) => ({
          url: `${baseUrl}/series/${s.slug}`,
          lastModified: new Date(s.updatedAt),
          changeFrequency: "daily" as const,
          priority: 0.8,
        })),
      );
      if (result.data.length < SERIES_PAGE_SIZE) break; // last page
      page++;
    }
  } catch {
    // API unavailable at build time — skip series
  }

  // ── Recent episode pages (last 500) ───────────────────────────────────────
  // Covers newly published episodes that need quick indexing.
  // Episodes older than this appear via series → episode links anyway.
  let episodePages: MetadataRoute.Sitemap = [];
  try {
    const episodes = await getRecentEpisodes({ pageSize: 500 });
    episodePages = episodes
      .filter((ep) => ep.series?.slug)
      .map((ep) => ({
        url: `${baseUrl}/series/${ep.series!.slug}/${ep.episodeNumber}`,
        lastModified: new Date(ep.airedAt ?? ep.createdAt),
        changeFrequency: "monthly" as const,
        priority: 0.6,
      }));
  } catch {
    // API unavailable — skip episode pages
  }

  // ── Genre pages ───────────────────────────────────────────────────────────
  let genrePages: MetadataRoute.Sitemap = [];
  try {
    const genres = await getGenres();
    genrePages = genres.map((g) => ({
      url: `${baseUrl}/genres/${encodeURIComponent(g.name.toLowerCase())}`,
      lastModified: new Date(),
      changeFrequency: "weekly" as const,
      priority: 0.6,
    }));
  } catch {
    // API unavailable — skip genre pages
  }

  return [...staticPages, ...seriesPages, ...episodePages, ...genrePages];
}
