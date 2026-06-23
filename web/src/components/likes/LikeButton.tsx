"use client";

import { useSession } from "next-auth/react";
import { useEffect, useRef, useState } from "react";

interface Props {
  seriesSlug: string;
  seriesTitle: string;
  coverUrl?: string | null;
}

export default function LikeButton({ seriesSlug, seriesTitle, coverUrl }: Props) {
  const { data: session, status } = useSession();
  const [liked, setLiked] = useState(false);
  const [count, setCount] = useState<number | null>(null);
  const [loading, setLoading] = useState(false);
  const [showTooltip, setShowTooltip] = useState(false);
  const tooltipTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Fetch current state on mount / when session changes
  useEffect(() => {
    fetch(`/api/likes/${encodeURIComponent(seriesSlug)}`)
      .then((r) => r.json())
      .then((data: { liked?: boolean; count?: number }) => {
        setLiked(data.liked ?? false);
        setCount(data.count ?? 0);
      })
      .catch(() => {});
  }, [seriesSlug, session?.user?.id]);

  // Cleanup tooltip timer on unmount
  useEffect(() => () => { if (tooltipTimer.current) clearTimeout(tooltipTimer.current); }, []);

  const handleClick = async () => {
    if (!session?.user) {
      // Not logged in — show tooltip hint
      setShowTooltip(true);
      if (tooltipTimer.current) clearTimeout(tooltipTimer.current);
      tooltipTimer.current = setTimeout(() => setShowTooltip(false), 2500);
      return;
    }

    const wasLiked = liked;
    const prevCount = count ?? 0;

    // Optimistic update
    setLiked(!wasLiked);
    setCount(prevCount + (wasLiked ? -1 : 1));
    setLoading(true);

    try {
      const res = await fetch(`/api/likes/${encodeURIComponent(seriesSlug)}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ seriesTitle, coverUrl: coverUrl ?? null }),
      });

      if (res.ok) {
        const data = (await res.json()) as { liked: boolean; count: number };
        setLiked(data.liked);
        setCount(data.count);
      } else {
        // Revert on failure
        setLiked(wasLiked);
        setCount(prevCount);
      }
    } catch {
      setLiked(wasLiked);
      setCount(prevCount);
    } finally {
      setLoading(false);
    }
  };

  if (status === "loading") {
    return <div className="h-9 w-20 rounded-lg bg-abyss-3 animate-pulse" />;
  }

  return (
    <div className="relative inline-block">
      <button
        onClick={handleClick}
        disabled={loading}
        aria-label={liked ? "Quitar like" : "Me gusta"}
        aria-pressed={liked}
        className={`flex items-center gap-2 px-3 py-2 rounded-lg border text-sm font-medium
          transition-all duration-150 select-none disabled:opacity-60
          ${liked
            ? "border-rose-700/60 text-rose-400 bg-rose-950/40 hover:bg-rose-950/60"
            : "border-line-2 text-ink-2 hover:text-white hover:bg-abyss-3"
          }`}
      >
        {loading ? (
          <span className="w-4 h-4 rounded-full border-2 border-current border-t-transparent animate-spin" />
        ) : (
          <svg
            className={`w-4 h-4 transition-transform duration-150 ${liked ? "scale-110" : ""}`}
            viewBox="0 0 24 24"
            fill={liked ? "currentColor" : "none"}
            stroke="currentColor"
            strokeWidth={liked ? 0 : 1.75}
            strokeLinecap="round"
            strokeLinejoin="round"
          >
            <path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z" />
          </svg>
        )}
        <span className="tabular-nums">
          {count !== null ? count.toLocaleString("es-AR") : "—"}
        </span>
      </button>

      {/* Tooltip: not logged in */}
      {showTooltip && (
        <div
          role="tooltip"
          className="absolute bottom-full left-1/2 -translate-x-1/2 mb-2 px-3 py-1.5 rounded-lg
            bg-abyss-3 border border-line-2 text-xs text-white whitespace-nowrap shadow-xl
            pointer-events-none"
        >
          Inicia sesión para dar like
          <span
            className="absolute top-full left-1/2 -translate-x-1/2 border-4 border-transparent border-t-[var(--bg-3)]"
            aria-hidden
          />
        </div>
      )}
    </div>
  );
}
