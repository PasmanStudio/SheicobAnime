import type { MetadataRoute } from "next";
import { episodeSitemapPageCount } from "@/lib/episode-sitemaps";
import { siteUrl } from "@/lib/site-url";

export const dynamic = "force-dynamic";

export default async function robots(): Promise<MetadataRoute.Robots> {
  const baseUrl = siteUrl();

  // /sitemap.xml (series + estáticas + géneros) + un sitemap por cada 10k
  // episodios — así el catálogo entero es descubrible sin depender de que
  // Google crawlee serie por serie.
  const sitemaps = [`${baseUrl}/sitemap.xml`];
  try {
    const pages = await episodeSitemapPageCount();
    for (let p = 1; p <= pages; p++) {
      sitemaps.push(`${baseUrl}/sitemap-episodes/${p}.xml`);
    }
  } catch {
    // API caída: robots.txt sigue sirviendo el sitemap principal
  }

  return {
    rules: [
      {
        userAgent: "*",
        allow: "/",
        disallow: ["/api/", "/admin/"],
      },
    ],
    sitemap: sitemaps,
  };
}
