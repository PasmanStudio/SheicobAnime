import type { Metadata } from "next";
import Link from "next/link";

export const metadata: Metadata = {
  title: "404 — Página no encontrada",
  description: "La página que buscas no existe.",
};

export default function NotFound() {
  return (
    <div className="container mx-auto px-4 py-24 text-center space-y-6">
      <p className="text-8xl font-black text-brand leading-none">404</p>
      <h1 className="text-2xl font-bold text-white">Página no encontrada</h1>
      <p className="text-ink-2 max-w-sm mx-auto">
        La página que buscas no existe o fue movida.
      </p>
      <div className="flex justify-center gap-3 pt-2">
        <Link
          href="/"
          className="px-5 py-2.5 rounded-lg bg-brand text-[var(--text-on-accent)] hover:brightness-110 text-sm font-medium transition-colors"
        >
          Volver al inicio
        </Link>
        <Link
          href="/search"
          className="px-5 py-2.5 rounded-lg border border-line-2 hover:border-line-2 text-ink-2 hover:text-white text-sm font-medium transition-colors"
        >
          Explorar anime
        </Link>
      </div>
    </div>
  );
}
