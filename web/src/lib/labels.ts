// Etiquetas compartidas de tipo/estado de serie y helper de tiempo relativo.
// Centralizado para no repetir los mismos mapas en 7 componentes (DRY) — y para
// que un cambio de copy o de timezone se haga en un solo lugar.

import type { SeriesStatus, SeriesType } from "@/lib/types";

export const TYPE_LABELS: Record<SeriesType, string> = {
  tv: "Serie",
  movie: "Película",
  ova: "OVA",
  ona: "ONA",
  special: "Especial",
};

export const STATUS_LABELS: Record<SeriesStatus, string> = {
  ongoing: "En emisión",
  completed: "Concluido",
  upcoming: "Por estrenar",
  hiatus: "En pausa",
};

export function typeLabel(t: SeriesType | null | undefined): string {
  return t ? (TYPE_LABELS[t] ?? t) : "";
}

// Rótulo lindo del servidor en el botón del player. "voe-sa" es nuestra subida
// propia a Voe (se muestra como "Voe"). Fallback: el provider crudo.
const SERVER_LABELS: Record<string, string> = {
  seekstreaming: "Sheicob",
  doodstream: "DoodStream",
  "voe-sa": "Voe",
  voe: "Voe",
  streamwish: "StreamWish",
  filemoon: "Filemoon",
  vidhide: "VidHide",
  okru: "OK.ru",
  mp4upload: "MP4Upload",
  mixdrop: "Mixdrop",
  mediafire: "MediaFire",
};

export function serverLabel(provider: string): string {
  return SERVER_LABELS[provider.toLowerCase()] ?? provider;
}

// El sitio corre en Cloudflare Workers (UTC). Las fechas "humanas" se muestran
// en hora argentina, no UTC.
export const SITE_TZ = "America/Argentina/Buenos_Aires";

/** "recién" / "hace N min" / "hace N h" / "ayer" / "hace N días". */
export function timeAgo(iso: string): string {
  const mins = Math.floor((Date.now() - new Date(iso).getTime()) / 60_000);
  if (mins < 1) return "recién";
  if (mins < 60) return `hace ${mins} min`;
  const h = Math.floor(mins / 60);
  if (h < 24) return `hace ${h} h`;
  const d = Math.floor(h / 24);
  return d === 1 ? "ayer" : `hace ${d} días`;
}
