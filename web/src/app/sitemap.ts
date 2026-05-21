import type { MetadataRoute } from "next";
import { getSeries, getGenres } from "@/lib/api";

export const dynamic = "force-dynamic";

export default async function sitemap(): Promise<MetadataRoute.Sitemap> {
  const baseUrl = process.env.NEXT_PUBLIC_SITE_URL ?? "https://sheicobanime.com";

  // Static pages
  const staticPages: MetadataRoute.Sitemap = [
    { url: baseUrl, lastModified: new Date(), changeFrequency: "daily", priority: 1.0 },
    { url: `${baseUrl}/temporada`, lastModified: new Date(), changeFrequency: "daily", priority: 0.9 },
    { url: `${baseUrl}/genres`, lastModified: new Date(), changeFrequency: "weekly", priority: 0.7 },
    { url: `${baseUrl}/search`, lastModified: new Date(), changeFrequency: "weekly", priority: 0.5 },
  ];

  // Dynamic series pages
  let seriesPages: MetadataRoute.Sitemap = [];
  try {
    const series = await getSeries({ pageSize: 100 });
    seriesPages = series.data.map((s) => ({
      url: `${baseUrl}/series/${s.slug}`,
      lastModified: new Date(s.updatedAt),
      changeFrequency: "daily" as const,
      priority: 0.8,
    }));
  } catch {
    // API unavailable at build — return static pages only
  }

  // Genre pages
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

  return [...staticPages, ...seriesPages, ...genrePages];
}
