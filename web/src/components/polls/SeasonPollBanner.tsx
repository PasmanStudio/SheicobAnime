"use client";

import type { ActivePoll } from "@/app/api/polls/active/route";
import Link from "next/link";
import { useEffect, useState } from "react";

/**
 * Banner del home que aparece solo cuando hay una encuesta de temporada activa.
 * Se oculta solo si no hay encuesta vigente.
 */
export default function SeasonPollBanner() {
  const [poll, setPoll] = useState<ActivePoll | null>(null);

  useEffect(() => {
    let cancelled = false;
    fetch("/api/polls/active")
      .then((r) => r.json())
      .then((d: { poll: ActivePoll | null }) => {
        if (!cancelled) setPoll(d.poll);
      })
      .catch(() => {});
    return () => { cancelled = true; };
  }, []);

  if (!poll) return null;

  const voted = poll.myVote !== null;

  return (
    <Link
      href="/encuestas"
      className="sh-speedlines group flex items-center gap-4 overflow-hidden rounded-card border border-[var(--accent-border)] bg-abyss-1 p-4 transition-all duration-fast hover:border-brand"
    >
      <span
        className="flex h-12 w-12 shrink-0 items-center justify-center rounded-btn text-[var(--text-on-accent)]"
        style={{ background: "var(--grad-action)" }}
      >
        <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
          <path d="M18 20V10M12 20V4M6 20v-6" />
        </svg>
      </span>
      <div className="min-w-0 flex-1">
        <span className="sh-label block">Encuesta de temporada</span>
        <span className="sh-title block truncate text-[15px]">{poll.title}</span>
      </div>
      <span className="shrink-0 rounded-btn px-4 py-2 text-[13px] font-bold text-[var(--text-on-accent)] transition-all duration-fast group-hover:brightness-110" style={{ background: "var(--grad-action)" }}>
        {voted ? "Ver resultados" : "Votar"}
      </span>
    </Link>
  );
}
