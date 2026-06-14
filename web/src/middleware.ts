import { NextResponse, type NextRequest } from "next/server";

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
