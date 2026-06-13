"use client";

import LoginModal from "@/components/auth/LoginModal";
import type { ActivePoll } from "@/app/api/polls/active/route";
import { useSession } from "next-auth/react";
import Image from "next/image";
import { useEffect, useState } from "react";

export default function SeasonPoll() {
  const { data: session } = useSession();
  const [poll, setPoll] = useState<ActivePoll | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [voting, setVoting] = useState(false);
  const [showLogin, setShowLogin] = useState(false);

  const load = () => {
    fetch("/api/polls/active")
      .then((r) => r.json())
      .then((d: { poll: ActivePoll | null }) => {
        setPoll(d.poll);
        setLoaded(true);
      })
      .catch(() => setLoaded(true));
  };

  useEffect(load, []);

  if (!loaded) return null;
  if (!poll) {
    return (
      <div className="rounded-card border border-line-1 bg-abyss-2 p-6 text-center text-sm">
        <p className="text-ink-2">No hay ninguna encuesta activa ahora.</p>
        <p className="mt-1 text-ink-3">Volvé al inicio de cada temporada para votar el mejor estreno.</p>
      </div>
    );
  }

  const voted = poll.myVote !== null;
  const showResults = voted;

  async function vote(optionId: string) {
    if (!session?.user) {
      setShowLogin(true);
      return;
    }
    if (voting) return;
    setVoting(true);
    try {
      const res = await fetch(`/api/polls/${poll!.id}/vote`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ optionId }),
      });
      if (res.ok) load(); // recargar para ver resultados + mi voto
    } finally {
      setVoting(false);
    }
  }

  return (
    <>
      <div className="flex flex-col gap-3">
        {poll.options.map((o) => {
          const pct = poll.totalVotes > 0 ? Math.round((o.votes / poll.totalVotes) * 100) : 0;
          const mine = poll.myVote === o.id;
          return (
            <button
              key={o.id}
              onClick={() => vote(o.id)}
              disabled={voting}
              className={`group relative flex items-center gap-3 overflow-hidden rounded-card border p-2.5 text-left transition-all duration-fast ${
                mine
                  ? "border-[var(--accent-border)] bg-abyss-2"
                  : "border-line-1 bg-abyss-2 hover:border-line-2"
              }`}
            >
              {/* Barra de resultado de fondo */}
              {showResults && (
                <span
                  className="absolute inset-y-0 left-0 bg-[var(--accent-muted)] transition-[width] duration-base"
                  style={{ width: `${pct}%` }}
                />
              )}
              <div className="relative z-10 h-16 w-12 shrink-0 overflow-hidden rounded-badge bg-abyss-3">
                {o.cover_url ? (
                  <Image src={o.cover_url} alt={o.series_title} fill sizes="48px" className="object-cover" />
                ) : (
                  <div className="flex h-full w-full items-center justify-center font-display italic font-black text-ink-3">
                    {o.series_title.trim()[0]?.toUpperCase() ?? "?"}
                  </div>
                )}
              </div>
              <span className="relative z-10 flex-1 min-w-0">
                <span className="sh-title block truncate text-sm">{o.series_title}</span>
                {showResults && (
                  <span className="sh-stat mt-0.5 block text-xs text-ink-3">
                    {o.votes} voto{o.votes !== 1 ? "s" : ""} · {pct}%
                  </span>
                )}
              </span>
              {mine && (
                <span className="relative z-10 shrink-0 rounded-badge bg-[var(--accent-muted)] px-2 py-0.5 font-mono text-[10px] font-bold uppercase tracking-[0.08em] text-brand-bright">
                  Tu voto
                </span>
              )}
            </button>
          );
        })}
      </div>

      <p className="mt-3 sh-stat text-[11px] text-ink-3">
        {poll.totalVotes} voto{poll.totalVotes !== 1 ? "s" : ""} en total
        {voted ? " · podés cambiar tu voto tocando otra opción" : " · +10 XP por votar"}
      </p>

      {showLogin && <LoginModal onClose={() => setShowLogin(false)} />}
    </>
  );
}
