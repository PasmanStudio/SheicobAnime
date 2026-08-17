import type { MetadataRoute } from "next";
import { siteUrl } from "@/lib/site-url";

// ESTÁTICO a propósito (antes: force-dynamic + fetch a la API).
// robots.txt lo pide cada bot en cada visita y la versión dinámica llamaba a
// /episodes/sitemap en CADA hit — un COUNT(*) sobre ~70k episodios contra
// Supabase. Bajo ráfaga de crawlers eso saturaba el pool de conexiones del API
// en Render y devolvía 500. Sin fetch no hay carga: se sirve desde el build.
export const dynamic = "force-static";

/**
 * Bots que no aportan tráfico y crawlean agresivo (SEO tools, scrapers de IA,
 * agregadores). Se bloquean enteros: cada uno de sus hits en una página fría
 * es un render del Worker + 2-4 llamadas al API + writes a KV.
 */
const BLOCKED_BOTS = [
  "AhrefsBot",
  "SemrushBot",
  "MJ12bot",
  "DotBot",
  "DataForSeoBot",
  "BLEXBot",
  "PetalBot",
  "Bytespider",
  "Barkrowler",
  "serpstatbot",
  "ZoominfoBot",
  "ImagesiftBot",
  "magpie-crawler",
  "SeekportBot",
  "GPTBot",
  "CCBot",
  "ClaudeBot",
  "Amazonbot",
  "Applebot-Extended",
  "meta-externalagent",
];

export default function robots(): MetadataRoute.Robots {
  const baseUrl = siteUrl();

  return {
    rules: [
      {
        userAgent: "*",
        allow: "/",
        // /search genera infinitas URLs por query string (crawl trap) y no
        // tiene valor de indexación; /api/ y /admin/ nunca deben crawlearse.
        disallow: ["/api/", "/admin/", "/search"],
        // Google lo ignora, pero Bing/Yandex/otros lo respetan.
        crawlDelay: 10,
      },
      {
        userAgent: BLOCKED_BOTS,
        disallow: "/",
      },
    ],
    // Solo el sitemap principal (estáticas + series + episodios de la semana).
    // Los 7 sitemaps de 10k episodios cada uno se dieron de baja: alimentaban a
    // Googlebot con ~70k URLs frías y el long tail se crawleaba 24/7. Los
    // episodios siguen siendo indexables por links internos desde /series/{slug}.
    sitemap: `${baseUrl}/sitemap.xml`,
  };
}
