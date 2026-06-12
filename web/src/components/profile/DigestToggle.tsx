"use client";

import { useEffect, useState } from "react";

/**
 * Toggle del digest semanal por email (perfil propio).
 * Domingo 18:00: episodios de tus series seguidas + top de la semana.
 */
export default function DigestToggle() {
  const [optIn, setOptIn] = useState<boolean | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let cancelled = false;
    fetch("/api/digest")
      .then((r) => r.json())
      .then((d: { optIn?: boolean }) => {
        if (!cancelled) setOptIn(Boolean(d.optIn));
      })
      .catch(() => {
        if (!cancelled) setOptIn(false);
      });
    return () => { cancelled = true; };
  }, []);

  async function handleToggle() {
    if (busy || optIn === null) return;
    setBusy(true);
    try {
      const res = await fetch("/api/digest", { method: "POST" });
      const d = (await res.json()) as { optIn?: boolean };
      setOptIn(Boolean(d.optIn));
    } catch {
      // estado queda como estaba
    } finally {
      setBusy(false);
    }
  }

  if (optIn === null) return null;

  return (
    <button
      onClick={handleToggle}
      disabled={busy}
      className={`flex items-center gap-2.5 rounded-btn border px-4 py-2.5 text-sm font-semibold transition-all duration-fast ${
        optIn
          ? "border-[var(--accent-border)] bg-[var(--accent-muted)] text-brand-bright"
          : "border-line-1 bg-abyss-2 text-ink-2 hover:border-line-2 hover:text-ink-1"
      }`}
      title="Episodios de tus series seguidas + top de la semana, los domingos. Un click para desuscribirte."
    >
      <span
        className={`inline-block h-2 w-2 rounded-full ${optIn ? "bg-[var(--success)]" : "bg-[var(--text-3)]"}`}
      />
      Digest semanal por email: {optIn ? "activado" : "desactivado"}
    </button>
  );
}
