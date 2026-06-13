"use client";

import type { NotificationRow } from "@/app/api/notifications/route";
import { useSession } from "next-auth/react";
import Link from "next/link";
import { useEffect, useRef, useState } from "react";

const TYPE_LABEL: Record<string, string> = {
  new_episode: "Episodio nuevo",
  comment_reply: "Respuesta",
  xp_level_up: "Subiste de nivel",
  badge_earned: "Badge",
  mention: "Mención",
  poll: "Encuesta",
  system: "SheicobAnime",
};

function timeAgo(iso: string): string {
  const mins = Math.floor((Date.now() - new Date(iso).getTime()) / 60_000);
  if (mins < 1) return "recién";
  if (mins < 60) return `hace ${mins} min`;
  const h = Math.floor(mins / 60);
  if (h < 24) return `hace ${h} h`;
  const d = Math.floor(h / 24);
  return d === 1 ? "ayer" : `hace ${d} días`;
}

export default function NotificationBell() {
  const { data: session } = useSession();
  const [open, setOpen] = useState(false);
  const [items, setItems] = useState<NotificationRow[]>([]);
  const [unread, setUnread] = useState(0);
  const ref = useRef<HTMLDivElement>(null);

  const load = () => {
    fetch("/api/notifications")
      .then((r) => r.json())
      .then((d: { items?: NotificationRow[]; unread?: number }) => {
        setItems(d.items ?? []);
        setUnread(d.unread ?? 0);
      })
      .catch(() => {});
  };

  // Carga inicial + polling cada 60s mientras la sesión esté activa
  useEffect(() => {
    if (!session?.user) return;
    load();
    const id = setInterval(load, 60_000);
    return () => clearInterval(id);
  }, [session?.user]);

  // Cerrar al click afuera
  useEffect(() => {
    const onClick = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", onClick);
    return () => document.removeEventListener("mousedown", onClick);
  }, []);

  if (!session?.user) return null;

  const handleOpen = () => {
    const next = !open;
    setOpen(next);
    if (next && unread > 0) {
      // Marcar todas como leídas al abrir
      fetch("/api/notifications", { method: "POST", body: "{}" }).catch(() => {});
      setUnread(0);
    }
  };

  return (
    <div ref={ref} className="relative">
      <button
        onClick={handleOpen}
        aria-label="Notificaciones"
        className="relative flex h-10 w-10 items-center justify-center rounded-btn text-ink-2 hover:text-ink-1 hover:bg-abyss-3 transition-colors duration-fast"
      >
        <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
          <path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9" />
          <path d="M10.3 21a1.94 1.94 0 0 0 3.4 0" />
        </svg>
        {unread > 0 && (
          <span className="absolute right-1.5 top-1.5 flex min-w-[16px] items-center justify-center rounded-full bg-[var(--danger)] px-1 font-mono text-[9px] font-bold leading-none text-white" style={{ height: 16 }}>
            {unread > 9 ? "9+" : unread}
          </span>
        )}
      </button>

      {open && (
        <div className="absolute right-0 top-full mt-1 w-80 max-w-[calc(100vw-1rem)] overflow-hidden rounded-card border border-line-2 bg-abyss-2 shadow-overlay z-50">
          <div className="border-b border-line-1 px-3 py-2.5">
            <span className="sh-label !text-[10px] !text-ink-3">Notificaciones</span>
          </div>
          {items.length === 0 ? (
            <div className="px-3 py-8 text-center text-sm">
              <p className="text-ink-2">No tenés notificaciones.</p>
              <p className="mt-1 text-ink-3">Seguí una serie y te avisamos cuando salga el próximo.</p>
            </div>
          ) : (
            <div className="max-h-[60vh] overflow-y-auto">
              {items.map((n) => {
                const inner = (
                  <>
                    <span className="sh-label !text-[9px] !tracking-[0.08em] !text-brand-bright">
                      {TYPE_LABEL[n.type] ?? "SheicobAnime"}
                    </span>
                    <p className="mt-0.5 text-[13px] font-semibold text-ink-1 line-clamp-1">{n.title}</p>
                    {n.body && <p className="text-xs text-ink-2 line-clamp-2">{n.body}</p>}
                    <span className="sh-stat mt-1 block text-[10px] text-ink-3">{timeAgo(n.created_at)}</span>
                  </>
                );
                const cls = `block border-b border-line-1 px-3 py-2.5 transition-colors duration-fast hover:bg-abyss-3 ${
                  n.read_at ? "" : "bg-[var(--accent-muted)]"
                }`;
                return n.url ? (
                  <Link key={n.id} href={n.url} className={cls} onClick={() => setOpen(false)}>
                    {inner}
                  </Link>
                ) : (
                  <div key={n.id} className={cls}>{inner}</div>
                );
              })}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
