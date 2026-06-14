"use client";

import { useEffect } from "react";

const TOKEN = process.env.NEXT_PUBLIC_CF_ANALYTICS_TOKEN ?? "";

/**
 * Cloudflare Web Analytics — gratis, cookieless, sin PII y sin necesidad de
 * consentimiento. Da visibilidad de tráfico (visitas, país, dispositivo, páginas
 * top) que hoy no existe: sin esto se optimiza a ciegas.
 *
 * No-op si NEXT_PUBLIC_CF_ANALYTICS_TOKEN no está seteado. El token se obtiene en
 * Cloudflare Dashboard → Analytics & Logs → Web Analytics → Add a site (es público:
 * viaja en el tag del beacon, no es secreto).
 */
export default function Analytics() {
  useEffect(() => {
    if (!TOKEN) return;
    if (document.querySelector("script[data-cf-beacon]")) return;

    const s = document.createElement("script");
    s.defer = true;
    s.src = "https://static.cloudflareinsights.com/beacon.min.js";
    // spa:true para contar también las navegaciones del App Router (client-side)
    s.setAttribute("data-cf-beacon", JSON.stringify({ token: TOKEN, spa: true }));
    document.body.appendChild(s);
  }, []);

  return null;
}
