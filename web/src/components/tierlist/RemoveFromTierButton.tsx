"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";

interface Props {
  tierListId: string;
  seriesSlug: string;
}

export default function RemoveFromTierButton({ tierListId, seriesSlug }: Props) {
  const router = useRouter();
  const [pending, setPending] = useState(false);

  const handleRemove = async () => {
    setPending(true);
    try {
      await fetch(
        `/api/tierlist/${tierListId}/entries?slug=${encodeURIComponent(seriesSlug)}`,
        { method: "DELETE" },
      );
      router.refresh();
    } finally {
      setPending(false);
    }
  };

  return (
    <button
      onClick={handleRemove}
      disabled={pending}
      title="Quitar de la lista"
      className="absolute -top-1.5 -left-1.5 z-10 w-4 h-4 rounded-full bg-neutral-800 border border-neutral-600 text-neutral-400 hover:text-white hover:bg-red-700 hover:border-red-600 flex items-center justify-center text-[9px] leading-none transition-colors disabled:opacity-50 opacity-0 group-hover:opacity-100"
    >
      {pending ? (
        <span className="w-2 h-2 rounded-full border border-current border-t-transparent animate-spin" />
      ) : (
        "✕"
      )}
    </button>
  );
}
