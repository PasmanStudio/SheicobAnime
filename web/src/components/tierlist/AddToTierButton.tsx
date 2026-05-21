"use client";

import { TIERS, TIER_COLORS, type Tier, type TierListSummary } from "@/lib/tierlist";
import { useSession } from "next-auth/react";
import { useEffect, useRef, useState } from "react";

interface Props {
  seriesSlug: string;
  seriesTitle: string;
  coverUrl?: string | null;
}

export default function AddToTierButton({ seriesSlug, seriesTitle, coverUrl }: Props) {
  const { data: session, status } = useSession();
  const [open, setOpen] = useState(false);
  const [lists, setLists] = useState<TierListSummary[]>([]);
  const [inTierLists, setInTierLists] = useState<Record<string, Tier>>({});
  const [loading, setLoading] = useState(false);
  const [activePicker, setActivePicker] = useState<string | null>(null); // list id showing tier picker
  const [pending, setPending] = useState<string | null>(null);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
        setActivePicker(null);
      }
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, []);

  const fetchLists = async () => {
    setLoading(true);
    try {
      const res = await fetch(`/api/tierlist?seriesSlug=${encodeURIComponent(seriesSlug)}`);
      if (res.ok) {
        const data = (await res.json()) as { lists: TierListSummary[]; in_tier_lists: Record<string, Tier> };
        setLists(data.lists);
        setInTierLists(data.in_tier_lists);
      }
    } catch { /* silent */ }
    finally { setLoading(false); }
  };

  const handleOpen = () => {
    if (!open) {
      fetchLists();
      setActivePicker(null);
    }
    setOpen((v) => !v);
  };

  const handleSelectTier = async (listId: string, tier: Tier) => {
    setPending(listId);
    try {
      const currentTier = inTierLists[listId];
      if (currentTier === tier) {
        // Remove from list
        await fetch(`/api/tierlist/${listId}/entries?slug=${encodeURIComponent(seriesSlug)}`, { method: "DELETE" });
        setInTierLists((prev) => { const next = { ...prev }; delete next[listId]; return next; });
        setLists((prev) => prev.map((l) => l.id === listId ? { ...l, entry_count: l.entry_count - 1 } : l));
      } else {
        // Upsert
        await fetch(`/api/tierlist/${listId}/entries`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ seriesSlug, seriesTitle, coverUrl: coverUrl ?? null, tier }),
        });
        if (!currentTier) {
          setLists((prev) => prev.map((l) => l.id === listId ? { ...l, entry_count: l.entry_count + 1 } : l));
        }
        setInTierLists((prev) => ({ ...prev, [listId]: tier }));
      }
    } catch { /* silent */ }
    finally {
      setPending(null);
      setActivePicker(null);
    }
  };

  const addedCount = Object.keys(inTierLists).length;

  if (status === "loading") return <div className="h-9 w-24 rounded-lg bg-neutral-800 animate-pulse" />;
  if (!session?.user) return null;

  return (
    <div className="relative inline-block" ref={ref}>
      <button
        onClick={handleOpen}
        className={`flex items-center gap-1.5 px-3 py-2 rounded-lg border text-sm font-medium transition-colors hover:bg-neutral-800
          ${addedCount > 0 ? "border-amber-600/60 text-amber-300" : "border-neutral-700 text-neutral-300"}`}
        aria-haspopup="listbox"
        aria-expanded={open}
      >
        <span>🏆</span>
        {addedCount > 0 ? `En ${addedCount} tier list${addedCount > 1 ? "s" : ""}` : "Tier List"}
        <svg className="w-3.5 h-3.5 shrink-0 opacity-60" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
        </svg>
      </button>

      {open && (
        <div className="absolute z-50 left-0 top-full mt-1 w-64 rounded-xl bg-neutral-900 border border-neutral-700 shadow-2xl overflow-hidden">
          {loading ? (
            <div className="px-3 py-4 flex justify-center">
              <span className="w-4 h-4 rounded-full border-2 border-neutral-500 border-t-transparent animate-spin" />
            </div>
          ) : lists.length === 0 ? (
            <div className="px-3 py-4 text-sm text-neutral-500 text-center">
              No tenés tier lists.<br />
              <a href="/tierlist" className="text-indigo-400 hover:text-indigo-300 underline text-xs mt-1 inline-block">
                Crear una →
              </a>
            </div>
          ) : (
            lists.map((list) => {
              const currentTier = inTierLists[list.id];
              const isPending = pending === list.id;
              const isPickerOpen = activePicker === list.id;
              const colors = currentTier ? TIER_COLORS[currentTier] : null;

              return (
                <div key={list.id} className="border-b border-neutral-800 last:border-0">
                  <button
                    onClick={() => setActivePicker(isPickerOpen ? null : list.id)}
                    disabled={isPending}
                    className="w-full flex items-center gap-2.5 px-3 py-2.5 hover:bg-neutral-800 transition-colors disabled:opacity-60"
                  >
                    {isPending ? (
                      <span className="w-5 h-5 rounded-full border-2 border-neutral-400 border-t-transparent animate-spin shrink-0" />
                    ) : currentTier && colors ? (
                      <span className={`w-5 h-5 rounded flex items-center justify-center text-[11px] font-extrabold shrink-0 ${colors.bg} ${colors.text}`}>
                        {currentTier}
                      </span>
                    ) : (
                      <span className="w-5 h-5 rounded border border-neutral-600 shrink-0" />
                    )}
                    <span className="text-sm text-neutral-300 truncate flex-1 text-left">{list.name}</span>
                    <svg className={`w-3.5 h-3.5 text-neutral-500 shrink-0 transition-transform ${isPickerOpen ? "rotate-180" : ""}`} fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
                    </svg>
                  </button>

                  {/* Tier picker row */}
                  {isPickerOpen && (
                    <div className="flex items-center gap-1 px-3 pb-2.5 flex-wrap">
                      {TIERS.map((tier) => {
                        const c = TIER_COLORS[tier];
                        const isActive = tier === currentTier;
                        return (
                          <button
                            key={tier}
                            onClick={() => handleSelectTier(list.id, tier)}
                            title={c.label}
                            className={`w-8 h-8 rounded flex items-center justify-center text-sm font-extrabold transition-all
                              ${c.bg} ${c.text}
                              ${isActive ? "ring-2 ring-white/60 scale-110" : "opacity-80 hover:opacity-100 hover:scale-105"}`}
                          >
                            {tier}
                          </button>
                        );
                      })}
                      {currentTier && (
                        <button
                          onClick={() => handleSelectTier(list.id, currentTier)}
                          className="ml-auto text-[10px] text-neutral-500 hover:text-red-400 transition-colors"
                        >
                          Quitar
                        </button>
                      )}
                    </div>
                  )}
                </div>
              );
            })
          )}
        </div>
      )}
    </div>
  );
}
