"use client";

import Link from "next/link";
import { useEffect, useState } from "react";

const CONSENT_KEY = "sheicob_ad_consent";
// v2: pasó de opt-in global (mataba los ingresos en AR) a geo-aware.
const CONSENT_VERSION = "2";

type Mode = "eu" | "row";

export interface ConsentState {
  given: boolean;
  version: string;
  timestamp: number;
}

/** Lee el modo de consentimiento que dejó el middleware. Sin cookie → "row" (ads on). */
function readGeoMode(): Mode {
  if (typeof document === "undefined") return "row";
  const m = document.cookie.match(/(?:^|;\s*)sheicob_ads_geo=(eu|row)/);
  return (m?.[1] as Mode) ?? "row";
}

function readStored(): ConsentState | null {
  if (typeof window === "undefined") return null;
  try {
    const raw = localStorage.getItem(CONSENT_KEY);
    if (!raw) return null;
    const s = JSON.parse(raw) as ConsentState;
    if (s.version !== CONSENT_VERSION) return null;
    return s;
  } catch {
    return null;
  }
}

/**
 * ¿Pueden cargar anuncios para este visitante?
 * - Fuera de la UE/EEE/UK (la audiencia AR): SÍ por defecto, salvo opt-out explícito.
 * - En la UE/EEE/UK: solo con opt-in explícito (GDPR).
 */
export function hasAdConsent(): boolean {
  if (typeof window === "undefined") return false;
  const stored = readStored();
  if (readGeoMode() === "row") {
    return stored ? stored.given : true; // default ON, respeta un rechazo
  }
  return stored?.given === true; // UE: opt-in estricto
}

/** Guarda la decisión del usuario. */
export function saveAdConsent(given: boolean): void {
  if (typeof window === "undefined") return;
  const s: ConsentState = { given, version: CONSENT_VERSION, timestamp: Date.now() };
  try {
    localStorage.setItem(CONSENT_KEY, JSON.stringify(s));
  } catch {
    /* storage unavailable */
  }
}

/**
 * Banner de consentimiento / aviso de cookies.
 *
 * - UE/EEE/UK: gate de opt-in (los ads no cargan hasta "Aceptar").
 * - Resto del mundo: aviso liviano e informativo — los ads YA cargan, esto no
 *   los bloquea. Se puede rechazar (opt-out) en un clic.
 *
 * Mobile: se ubica por encima de la bottom nav fija para no taparla.
 */
export default function ConsentBanner() {
  const [mode, setMode] = useState<Mode | null>(null);
  const [visible, setVisible] = useState(false);

  useEffect(() => {
    setMode(readGeoMode());
    setVisible(readStored() === null);
  }, []);

  if (!visible || mode === null) return null;

  // ── UE/EEE/UK: opt-in bloqueante ──
  if (mode === "eu") {
    const accept = () => {
      saveAdConsent(true);
      setVisible(false);
      window.location.reload(); // recargar para que los scripts de ads inicien
    };
    const decline = () => {
      saveAdConsent(false);
      setVisible(false);
    };
    return (
      <div className="fixed inset-x-0 bottom-14 z-50 border-t border-line-1 bg-abyss-2 p-4 shadow-lg md:bottom-0">
        <div className="mx-auto flex max-w-container flex-col items-center justify-between gap-3 sm:flex-row">
          <p className="text-[13px] leading-relaxed text-ink-2">
            Usamos cookies y publicidad para mantener el sitio gratis. Al aceptar,
            consentís anuncios personalizados.{" "}
            <Link href="/privacy" className="text-brand-bright underline hover:brightness-110">
              Política de privacidad
            </Link>
          </p>
          <div className="flex shrink-0 gap-2">
            <button
              onClick={decline}
              className="rounded-btn px-4 py-2 text-[13px] font-semibold text-ink-3 transition-colors duration-fast hover:text-ink-1"
            >
              Rechazar
            </button>
            <button
              onClick={accept}
              className="rounded-btn px-5 py-2 text-[13px] font-bold text-[var(--text-on-accent)] transition-all duration-fast hover:brightness-110 active:scale-[0.97]"
              style={{ background: "var(--grad-action)" }}
            >
              Aceptar
            </button>
          </div>
        </div>
      </div>
    );
  }

  // ── Resto del mundo (AR/LATAM): aviso liviano, NO bloquea anuncios ──
  const ack = () => {
    saveAdConsent(true);
    setVisible(false);
  };
  const optOut = () => {
    saveAdConsent(false);
    setVisible(false);
    window.location.reload(); // descargar cualquier script de ad ya inyectado
  };
  return (
    <div className="fixed inset-x-0 bottom-14 z-50 border-t border-line-1 bg-abyss-2 px-4 py-3 shadow-lg md:bottom-0">
      <div className="mx-auto flex max-w-container flex-col items-center justify-between gap-2.5 sm:flex-row">
        <p className="text-[12.5px] leading-relaxed text-ink-3">
          Usamos cookies y unos pocos anuncios para sostener los servidores y que el
          sitio siga gratis.{" "}
          <Link href="/privacy" className="text-brand-bright underline hover:brightness-110">
            Más info
          </Link>
        </p>
        <div className="flex shrink-0 items-center gap-3">
          <button
            onClick={optOut}
            className="text-[12.5px] font-medium text-ink-3 transition-colors duration-fast hover:text-ink-1"
          >
            Rechazar
          </button>
          <button
            onClick={ack}
            className="rounded-btn border border-line-2 bg-abyss-3 px-4 py-1.5 text-[12.5px] font-semibold text-ink-1 transition-all duration-fast hover:brightness-110 active:scale-[0.97]"
          >
            Entendido
          </button>
        </div>
      </div>
    </div>
  );
}
