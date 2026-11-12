import { NextResponse, type NextRequest } from "next/server";
import { getCloudflareContext } from "@opennextjs/cloudflare";

/**
 * Crawler de Meta que se hace pasar por Chrome.
 *
 * Nov-2026: el cupo de writes de KV (1000/día en el plan free) llegaba al 50-100 %
 * con apenas 400-900 requests diarias. Las subidas eran ráfagas de 1-3 h desde
 * el ASN de Meta (AS32934): 407 de 419 requests el 9-nov, sobre 371 páginas de
 * episodio distintas, todas frías → cada una escribe 2-4 keys en KV. Usa UA de
 * Chrome, así que el `Disallow` a meta-externalagent de robots.txt no lo frena.
 *
 * Se corta acá, antes del render: un 403 no toca la API ni KV. Se dejan pasar
 * los fetchers de vista previa de links (los stickers de las historias de
 * Instagram apuntan a páginas de episodio).
 */
const META_ASN = 32934;
const META_LINK_PREVIEW_UA = /facebookexternalhit|facebookcatalog|Facebot/i;

function isMetaCrawler(req: NextRequest): boolean {
  let asn: number | undefined;
  try {
    asn = getCloudflareContext().cf?.asn;
  } catch {
    // `next dev` sin contexto de Cloudflare: no hay ASN, no se bloquea.
    return false;
  }
  if (asn !== META_ASN) return false;
  return !META_LINK_PREVIEW_UA.test(req.headers.get("user-agent") ?? "");
}

/**
 * Geo-detección liviana para decidir el modo de consentimiento de anuncios.
 *
 * La audiencia de SheicobAnime es Argentina/LATAM (fuera del alcance de GDPR),
 * así que los anuncios cargan POR DEFECTO. Solo los visitantes de la UE/EEE/UK
 * — donde el opt-in de publicidad personalizada es exigible — reciben el gate
 * de consentimiento (lo decide el cliente leyendo esta cookie).
 *
 * Cloudflare agrega el header `CF-IPCountry` con el ISO-2 del visitante.
 * Si no hay header (dev local, otro hosting) → "row" (ads on). Fail-open
 * hacia los ingresos: nunca apagamos ads por una detección que falle.
 */

const CONSENT_REQUIRED = new Set([
  // UE
  "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR",
  "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL", "PL", "PT", "RO", "SK",
  "SI", "ES", "SE",
  // EEE / EFTA
  "IS", "LI", "NO",
  // Reino Unido
  "GB",
]);

const GEO_COOKIE = "sheicob_ads_geo";

export function middleware(req: NextRequest) {
  if (isMetaCrawler(req)) {
    return new NextResponse(null, { status: 403 });
  }

  const res = NextResponse.next();

  // Ya resuelto en una request anterior → no reescribir (mantiene las
  // respuestas cacheables sin un Set-Cookie por hit).
  if (req.cookies.has(GEO_COOKIE)) return res;

  const country = (req.headers.get("cf-ipcountry") || "").toUpperCase();
  const mode = CONSENT_REQUIRED.has(country) ? "eu" : "row";

  res.cookies.set(GEO_COOKIE, mode, {
    path: "/",
    maxAge: 60 * 60 * 24 * 180, // 180 días
    sameSite: "lax",
    secure: true,
    httpOnly: false, // legible por JS: el cliente decide si pedir consentimiento
  });

  return res;
}

export const config = {
  // Solo necesitamos la cookie en navegaciones de página: excluimos assets
  // estáticos, imágenes, fuentes y la API.
  matcher: [
    "/((?!_next/|api/|favicon|robots.txt|sitemap|manifest|.*\\.(?:png|jpg|jpeg|gif|svg|webp|ico|txt|xml|webmanifest|js|css|woff2?)).*)",
  ],
};
