"use client";

import { TIERS, TIER_COLORS, type Tier } from "@/lib/tierlist";
import Image from "next/image";
import { useRouter } from "next/navigation";
import { useRef, useState, useEffect } from "react";

interface Props {
  tierListId: string;
  seriesSlug: string;
  seriesTitle: string;
  coverUrl: string | null;
  currentTier: Tier;
}

export default function TierPickerOnEntry({
  tierListId,
  seriesSlug,
  seriesTitle,
  coverUrl,
  currentTier,
}: Props) {
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const [pending, setPending] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, []);

  const handleSelect = async (tier: Tier) => {
    setOpen(false);
    setPending(true);
    try {
      await fetch(`/api/tierlist/${tierListId}/entries`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ seriesSlug, seriesTitle, coverUrl, tier }),
      });
      router.refresh();
    } finally {
      setPending(false);
    }
  };

  const colors = TIER_COLORS[currentTier];

  return (
    <div className="relative" ref={ref}>
      <button
        onClick={() => setOpen((v) => !v)}
        disabled={pending}
        title={`Cambiar tier de ${seriesTitle}`}
        className="relative block w-14 group focus:outline-none"
      >
        <div className="relative aspect-[2/3] rounded overflow-hidden bg-neutral-800">
          {coverUrl ? (
            <Image
              src={coverUrl}
              alt={seriesTitle}
              fill
              sizes="56px"
              className="object-cover group-hover:opacity-70 transition-opacity"
            />
          ) : (
            <div className="w-full h-full flex items-center justify-center text-neutral-600 text-lg">🎬</div>
          )}
          {pending && (
            <div className="absolute inset-0 flex items-center justify-center bg-black/50">
              <span className="w-4 h-4 rounded-full border-2 border-white border-t-transparent animate-spin" />
            </div>
          )}
        </div>
        {/* Current tier badge */}
        <span
          className={`absolute -top-1.5 -right-1.5 w-5 h-5 rounded-full flex items-center justify-center text-[10px] font-extrabold shadow-md ${colors.bg} ${colors.text}`}
        >
          {currentTier}
        </span>
      </button>

      {open && (
        <div className="absolute z-50 top-full mt-1 left-1/2 -translate-x-1/2 bg-neutral-900 border border-neutral-700 rounded-xl shadow-2xl overflow-hidden w-36">
          <p className="px-3 py-2 text-[10px] text-neutral-500 uppercase tracking-wider font-medium border-b border-neutral-800 truncate">
            {seriesTitle}
          </p>
          {TIERS.map((tier) => {
            const c = TIER_COLORS[tier];
            const isActive = tier === currentTier;
            return (
              <button
                key={tier}
                onClick={() => handleSelect(tier)}
                className={`w-full flex items-center gap-2 px-3 py-1.5 text-sm transition-colors hover:bg-neutral-800 ${isActive ? "opacity-50 cursor-default" : ""}`}
              >
                <span
                  className={`w-6 h-6 rounded flex items-center justify-center text-xs font-extrabold shrink-0 ${c.bg} ${c.text}`}
                >
                  {tier}
                </span>
                <span className="text-neutral-300 text-xs">{c.label}</span>
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}
