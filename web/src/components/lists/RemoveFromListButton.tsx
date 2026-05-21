"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";

interface Props {
  listId: string;
  seriesSlug: string;
}

export default function RemoveFromListButton({ listId, seriesSlug }: Props) {
  const router = useRouter();
  const [loading, setLoading] = useState(false);

  const handleRemove = async () => {
    setLoading(true);
    try {
      await fetch(`/api/lists/${listId}/items?slug=${encodeURIComponent(seriesSlug)}`, {
        method: "DELETE",
      });
      router.refresh();
    } catch {
      // silently fail
    } finally {
      setLoading(false);
    }
  };

  return (
    <button
      onClick={handleRemove}
      disabled={loading}
      className="absolute top-1.5 right-1.5 z-10 flex items-center justify-center w-6 h-6 rounded-full
        bg-neutral-900/80 border border-neutral-700 text-neutral-400 hover:text-red-400 hover:border-red-700/50
        transition-colors disabled:opacity-50 text-xs backdrop-blur-sm"
      aria-label="Quitar de la lista"
      title="Quitar"
    >
      {loading ? (
        <span className="w-3 h-3 rounded-full border border-current border-t-transparent animate-spin" />
      ) : (
        "✕"
      )}
    </button>
  );
}
