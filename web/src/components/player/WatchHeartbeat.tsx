"use client";

import { updateWatchProgress } from "@/lib/api";
import { useEffect, useRef } from "react";

interface Props {
  episodeId: string;
  /** Duración real del episodio en segundos (si la conocemos) */
  durationSeconds?: number | null;
}

// Default cuando no conocemos la duración: ~24 min (1 episodio típico).
const DEFAULT_DURATION = 1440;
// Reportar cada N segundos de tiempo VISIBLE acumulado.
const REPORT_EVERY = 15;

/**
 * Heartbeat de progreso mirror-agnóstico.
 *
 * El player principal iframea mirrors de terceros (seekplayer.me, etc.) y no
 * podemos leer su currentTime por cross-origin. En vez de eso acumulamos el
 * tiempo que la pestaña estuvo VISIBLE en la página del episodio y lo
 * reportamos como posición — suficiente para poblar "Continuar viendo" y
 * "Seguir mirando" con cualquier mirror.
 *
 * Nunca auto-completa: la posición se topea al 85% de la duración, así el
 * episodio sigue apareciendo en "Continuar viendo". Marcar como visto es
 * explícito (botón MarkWatched).
 */
export default function WatchHeartbeat({ episodeId, durationSeconds }: Props) {
  const watchedRef = useRef(0); // segundos visibles acumulados
  const lastSentRef = useRef(0);

  useEffect(() => {
    if (!episodeId) return;
    const duration = durationSeconds && durationSeconds > 0 ? durationSeconds : DEFAULT_DURATION;
    const maxPos = Math.floor(duration * 0.85); // nunca llega al 90% (umbral "completado")

    const send = () => {
      const pos = Math.min(watchedRef.current, maxPos);
      if (pos < 10 || pos === lastSentRef.current) return;
      lastSentRef.current = pos;
      // best-effort — el progreso es un extra, nunca rompe la página
      void updateWatchProgress(episodeId, pos, duration).catch(() => {});
    };

    // Tick de 1s: suma tiempo solo si la pestaña está visible
    const tick = setInterval(() => {
      if (document.visibilityState !== "visible") return;
      watchedRef.current += 1;
      if (watchedRef.current - lastSentRef.current >= REPORT_EVERY) send();
    }, 1000);

    // Reportar al ocultar/cerrar la pestaña para no perder el último tramo
    const onHide = () => {
      if (document.visibilityState === "hidden") send();
    };
    document.addEventListener("visibilitychange", onHide);
    window.addEventListener("pagehide", send);

    return () => {
      clearInterval(tick);
      document.removeEventListener("visibilitychange", onHide);
      window.removeEventListener("pagehide", send);
      send(); // último reporte al desmontar (navegar a otro episodio/página)
    };
  }, [episodeId, durationSeconds]);

  return null;
}
