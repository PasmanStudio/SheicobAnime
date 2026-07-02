import { getEpisodeSitemapPage } from "@/lib/api";
import { EPISODES_PER_SITEMAP } from "@/lib/episode-sitemaps";
import { siteUrl } from "@/lib/site-url";

// XML a mano en vez de generateSitemaps() de Next: comportamiento predecible
// bajo OpenNext/Workers y /sitemap.xml sigue existiendo tal cual está enviado
// a Search Console. Los crawlers descubren estos archivos vía robots.txt.
export const dynamic = "force-dynamic";

const escapeXml = (s: string) =>
  s.replace(/[<>&'"]/g, (c) =>
    ({ "<": "&lt;", ">": "&gt;", "&": "&amp;", "'": "&apos;", '"': "&quot;" })[c]!
  );

export async function GET(
  _req: Request,
  { params }: { params: Promise<{ page: string }> }
) {
  const { page: pageStr } = await params;
  // Acepta "3" y "3.xml" (robots.ts publica la variante .xml)
  const page = Number(pageStr.replace(/\.xml$/, ""));
  if (!Number.isInteger(page) || page < 1) {
    return new Response("Not found", { status: 404 });
  }

  try {
    const { data } = await getEpisodeSitemapPage(page, EPISODES_PER_SITEMAP);
    if (data.length === 0) return new Response("Not found", { status: 404 });

    const base = siteUrl();
    const urls = data.map(
      (ep) =>
        `<url><loc>${base}/series/${escapeXml(ep.seriesSlug)}/${ep.episodeNumber}</loc>` +
        `<lastmod>${new Date(ep.createdAt).toISOString()}</lastmod>` +
        `<changefreq>monthly</changefreq><priority>0.6</priority></url>`
    );
    const xml =
      `<?xml version="1.0" encoding="UTF-8"?>\n` +
      `<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n` +
      urls.join("\n") +
      `\n</urlset>`;

    return new Response(xml, {
      headers: {
        "Content-Type": "application/xml; charset=utf-8",
        // Los crawlers releen sitemaps cada tanto — 1h en edge alcanza
        "Cache-Control": "public, max-age=3600",
      },
    });
  } catch {
    // API fría/caída: 503 para que el crawler reintente en vez de indexar un 404
    return new Response("Service unavailable", { status: 503 });
  }
}
