"use client";

import { useSession } from "next-auth/react";
import { useState } from "react";

interface Props {
  episodeId: string;
  seriesSlug: string;
  episodeNumber: number;
  episodeTitle?: string | null;
  seriesTitle?: string | null;
  coverUrl?: string | null;
}

export default function MarkWatchedButton({
  episodeId,
  seriesSlug,
  episodeNumber,
  episodeTitle,
  seriesTitle,
  coverUrl,
}: Props) {
  const { data: session, status } = useSession();
  const [watched, setWatched] = useState(false);
  const [loading, setLoading] = useState(false);

  if (status === "loading") return null;
  if (!session?.user) return null;

  const handleToggle = async () => {
    setLoading(true);
    try {
      if (watched) {
        await fetch(`/api/history/${encodeURIComponent(episodeId)}`, { method: "DELETE" });
        setWatched(false);
      } else {
        await fetch("/api/history", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            episodeId,
            seriesSlug,
            episodeNumber,
            episodeTitle: episodeTitle ?? null,
            seriesTitle: seriesTitle ?? null,
            coverUrl: coverUrl ?? null,
          }),
        });
        setWatched(true);
      }
    } catch {
      // silent
    } finally {
      setLoading(false);
    }
  };

  return (
    <button
      onClick={handleToggle}
      disabled={loading}
      className={`flex items-center gap-2 px-3 py-2 rounded-lg border text-sm font-medium transition-colors disabled:opacity-60
        ${watched
          ? "text-green-400 border-green-700/50 hover:border-red-700/50 hover:text-red-400"
          : "text-ink-2 border-line-2 hover:text-green-400 hover:border-green-700/50"
        }`}
      title={watched ? "Quitar de historial" : "Marcar como visto"}
    >
      {loading ? (
        <span className="w-4 h-4 rounded-full border-2 border-current border-t-transparent animate-spin" />
      ) : (
        <svg className="w-4 h-4 shrink-0" fill={watched ? "currentColor" : "none"} viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
        </svg>
      )}
      {watched ? "Visto" : "Marcar como visto"}
    </button>
  );
}
