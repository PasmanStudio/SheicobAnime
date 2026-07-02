import { getEpisodeSitemapPage } from "@/lib/api";

/**
 * Episodios por archivo de sitemap (/sitemap-episodes/{n}.xml).
 * Límite del protocolo: 50k URLs por archivo — 10k deja margen y coincide
 * con el pageSize máximo del endpoint /episodes/sitemap de la API.
 */
export const EPISODES_PER_SITEMAP = 10_000;

/**
 * Cantidad de archivos de sitemap de episodios que existen hoy.
 * Un fetch mínimo (pageSize=1) trae el total; el fetch cache de Next
 * lo comparte con robots.ts sin pegarle de más a la API.
 */
export async function episodeSitemapPageCount(): Promise<number> {
  const { total } = await getEpisodeSitemapPage(1, 1);
  return Math.max(1, Math.ceil(total / EPISODES_PER_SITEMAP));
}
