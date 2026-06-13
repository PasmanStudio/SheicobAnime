/**
 * Loader de imágenes para next/image.
 *
 * Los posters vienen de CDNs de terceros (cdn.jkdesa.com, etc.) a tamaño
 * completo — el mayor costo de performance en móvil (70% del tráfico). Este
 * loader los pasa por wsrv.nl (images.weserv.nl): un CDN de imágenes gratuito
 * y sin signup que redimensiona al ancho real del contenedor y entrega WebP.
 *
 * Funciona en cualquier hosting (no depende de Cloudflare Image Resizing, que
 * exige dominio propio + plan Pro). Escape hatch: NEXT_PUBLIC_DISABLE_IMG_PROXY=1
 * sirve las imágenes directo del origen.
 */

interface LoaderArgs {
  src: string;
  width: number;
  quality?: number;
}

export default function imageLoader({ src, width, quality }: LoaderArgs): string {
  // Assets locales (logo, favicon) y data URIs: directo — son chicos y ya
  // pasan por el CDN del sitio.
  if (src.startsWith("/") || src.startsWith("data:") || src.startsWith("blob:")) {
    return src;
  }

  if (process.env.NEXT_PUBLIC_DISABLE_IMG_PROXY === "1") {
    return src;
  }

  const params = new URLSearchParams({
    url: src,
    w: String(width),
    q: String(quality ?? 72),
    output: "webp",
    we: "1", // without-enlargement: nunca agranda un poster chico
  });
  return `https://wsrv.nl/?${params.toString()}`;
}
