"use client";

import {
  WATCH_STATUS_COLORS,
  WATCH_STATUS_ICONS,
  WATCH_STATUS_LABELS,
  WATCH_STATUSES,
  type WatchStatus,
} from "@/lib/watchlist";
import { useSession } from "next-auth/react";
import { useEffect, useRef, useState } from "react";

interface Props {
  seriesSlug: string;
  seriesTitle: string;
  coverUrl?: string | null;
}

export default function WatchlistButton({ seriesSlug, seriesTitle, coverUrl }: Props) {
  const { data: session, status } = useSession();
  const [currentStatus, setCurrentStatus] = useState<WatchStatus | null>(null);
  const [loading, setLoading] = useState(false);
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  // Fetch current status when logged in
  useEffect(() => {
    if (!session?.user?.id) return;
    fetch(`/api/watchlist/${encodeURIComponent(seriesSlug)}`)
      .then((r) => r.json())
      .then((data) => {
        if (data?.status) setCurrentStatus(data.status as WatchStatus);
      })
      .catch(() => {});
  }, [session?.user?.id, seriesSlug]);

  // Close on outside click
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, []);

  if (status === "loading") {
    return <div className="h-9 w-28 rounded-lg bg-abyss-3 animate-pulse" />;
  }
  if (!session?.user) return null; // hide if not logged in

  const handleSelect = async (newStatus: WatchStatus | null) => {
    setOpen(false);
    setLoading(true);
    try {
      const res = await fetch("/api/watchlist", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          seriesSlug,
          seriesTitle,
          coverUrl: coverUrl ?? null,
          status: newStatus,
        }),
      });
      // Only update UI if server confirmed the save (avoid false optimistic update)
      if (res.ok) {
        setCurrentStatus(newStatus);
      }
    } catch {
      // network error — don't update UI
    } finally {
      setLoading(false);
    }
  };

  const label = currentStatus
    ? `${WATCH_STATUS_ICONS[currentStatus]} ${WATCH_STATUS_LABELS[currentStatus]}`
    : "＋ Guardar";

  const colorClass = currentStatus ? WATCH_STATUS_COLORS[currentStatus] : "text-ink-2 border-line-2";

  return (
    <div className="relative inline-block" ref={ref}>
      <button
        onClick={() => setOpen((v) => !v)}
        disabled={loading}
        className={`flex items-center gap-1.5 px-3 py-2 rounded-lg border text-sm font-medium transition-colors
          hover:bg-abyss-3 disabled:opacity-60 ${colorClass}`}
        aria-haspopup="listbox"
        aria-expanded={open}
      >
        {loading ? (
          <span className="w-3.5 h-3.5 rounded-full border-2 border-current border-t-transparent animate-spin" />
        ) : null}
        {label}
        <svg className="w-3.5 h-3.5 shrink-0 opacity-60" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
        </svg>
      </button>

      {open && (
        <div
          className="absolute z-50 left-0 top-full mt-1 w-40 rounded-xl bg-abyss-2 border border-line-2 shadow-2xl overflow-hidden"
          role="listbox"
        >
          {WATCH_STATUSES.map((s) => (
            <button
              key={s}
              role="option"
              aria-selected={currentStatus === s}
              onClick={() => handleSelect(s)}
              className={`w-full flex items-center gap-2 px-3 py-2.5 text-sm transition-colors
                hover:bg-abyss-3
                ${currentStatus === s ? "text-white font-semibold" : "text-ink-2"}`}
            >
              <span className="w-4 text-center">{WATCH_STATUS_ICONS[s]}</span>
              {WATCH_STATUS_LABELS[s]}
              {currentStatus === s && (
                <svg className="ml-auto w-3.5 h-3.5 text-brand-bright" fill="currentColor" viewBox="0 0 20 20">
                  <path fillRule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z" clipRule="evenodd" />
                </svg>
              )}
            </button>
          ))}

          {currentStatus && (
            <>
              <div className="border-t border-line-1" />
              <button
                onClick={() => handleSelect(null)}
                className="w-full flex items-center gap-2 px-3 py-2.5 text-sm text-ink-3 hover:text-danger hover:bg-abyss-3 transition-colors"
              >
                <span className="w-4 text-center">✕</span>
                Quitar de lista
              </button>
            </>
          )}
        </div>
      )}
    </div>
  );
}
