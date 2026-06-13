"use client";

import { useEffect } from "react";

/**
 * Registra el service worker en cada visita — necesario para que el sitio sea
 * instalable (PWA) y para el shell offline. El push web reusa este mismo SW
 * (FollowButton solo pide el permiso + la suscripción).
 */
export default function ServiceWorkerRegister() {
  useEffect(() => {
    if (!("serviceWorker" in navigator)) return;
    // Registrar después del load para no competir con el render inicial
    const onLoad = () => {
      navigator.serviceWorker.register("/sw.js").catch(() => {
        // best-effort — sin SW el sitio funciona igual, solo sin PWA/offline
      });
    };
    if (document.readyState === "complete") onLoad();
    else window.addEventListener("load", onLoad, { once: true });
    return () => window.removeEventListener("load", onLoad);
  }, []);

  return null;
}
