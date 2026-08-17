"use client";

import * as Sentry from "@sentry/nextjs";
import Link from "next/link";
import { useEffect } from "react";

/**
 * Error boundary del layout del sitio.
 *
 * Sin esto, cualquier error de render en una página (típicamente el API en
 * Render devolviendo 500 o timeouteando en frío) subía hasta global-error.tsx,
 * que reemplaza el <html> entero por la pantalla genérica de Next: sin header,
 * sin nav, sin marca — el usuario ve el sitio "caído". Acá el fallo queda
 * contenido dentro del layout: se mantiene la navegación y hay un botón de
 * reintentar, que es lo que suele hacer falta porque estos errores son
 * transitorios (API despertando o saturada).
 */
export default function SiteError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  useEffect(() => {
    Sentry.captureException(error);
  }, [error]);

  return (
    <div className="container mx-auto px-4 py-24 text-center space-y-6">
      <p className="text-6xl font-black text-brand leading-none">¡Ups!</p>
      <h1 className="text-2xl font-bold text-white">
        No pudimos cargar esta página
      </h1>
      <p className="text-ink-2 max-w-md mx-auto">
        Estamos teniendo un problema temporal. Probá de nuevo en unos segundos.
      </p>
      <div className="flex flex-wrap justify-center gap-3 pt-2">
        <button
          type="button"
          onClick={reset}
          className="px-5 py-2.5 rounded-lg bg-brand text-[var(--text-on-accent)] hover:brightness-110 text-sm font-medium transition-colors"
        >
          Reintentar
        </button>
        <Link
          href="/"
          className="px-5 py-2.5 rounded-lg border border-line-2 hover:border-line-2 text-ink-2 hover:text-white text-sm font-medium transition-colors"
        >
          Volver al inicio
        </Link>
      </div>
    </div>
  );
}
