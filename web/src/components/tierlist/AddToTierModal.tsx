"use client";

import { TIERS, TIER_COLORS, type Tier } from "@/lib/tierlist";
import type { SeriesSuggest } from "@/lib/types";
import Image from "next/image";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useRef, useState } from "react";

interface Props {
  tierListId: string;
  existingSlugs: string[];
  onAdded?: (slug: string) => void;
}

const API_BASE_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000";

export default function AddToTierModal({ tierListId, existingSlugs, onAdded }: Props) {
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState("");
  const [results, setResults] = useState<SeriesSuggest[]>([]);
  const [loading, setLoading] = useState(false);
  const [selectedTier, setSelectedTier] = useState<Tier>("B");
  const [added, setAdded] = useState<Set<string>>(new Set(existingSlugs));
  const [pending, setPending] = useState<string | null>(null);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>();
  const inputRef = useRef<HTMLInputElement>(null);
  const dialogRef = useRef<HTMLDivElement>(null);

  // Focus on open
  useEffect(() => {
    if (open) {
      setTimeout(() => inputRef.current?.focus(), 50);
      setQ("");
      setResults([]);
    }
  }, [open]);

  // Close on Escape
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("keydown", handler);
    return () => document.removeEventListener("keydown", handler);
  }, []);

  // Close on outside click
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (dialogRef.current && !dialogRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    if (open) document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [open]);

  const search = useCallback(async (term: string) => {
    if (term.length < 2) {
      setResults([]);
      return;
    }
    setLoading(true);
    try {
      const res = await fetch(
        `${API_BASE_URL}/series/suggest?q=${encodeURIComponent(term)}`,
        { cache: "no-store" },
      );
      if (res.ok) {
        const data = (await res.json()) as SeriesSuggest[];
        setResults(data);
      }
    } catch {
      // silently ignore
    } finally {
      setLoading(false);
    }
  }, []);

  const handleInput = (value: string) => {
    setQ(value);
    clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => search(value.trim()), 280);
  };

  const handleAdd = async (series: SeriesSuggest) => {
    if (pending === series.slug) return;
    setPending(series.slug);
    try {
      const res = await fetch(`/api/tierlist/${tierListId}/entries`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          seriesSlug: series.slug,
          seriesTitle: series.title,
          coverUrl: series.coverUrl ?? null,
          tier: selectedTier,
        }),
      });
      if (res.ok) {
        setAdded((prev) => new Set([...prev, series.slug]));
        onAdded?.(series.slug);
        router.refresh(); // re-run RSC to include new entry in the tier grid
      }
    } catch {
      // silently ignore
    } finally {
      setPending(null);
    }
  };

  return (
    <>
      {/* Trigger button */}
      <button
        onClick={() => setOpen(true)}
        className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg bg-indigo-600 hover:bg-indigo-500 text-white text-sm font-medium transition-colors"
      >
        <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M12 4v16m8-8H4" />
        </svg>
        Agregar anime
      </button>

      {/* Modal overlay */}
      {open && (
        <div className="fixed inset-0 z-50 flex items-start justify-center pt-16 px-4 bg-black/70 backdrop-blur-sm">
          <div
            ref={dialogRef}
            className="w-full max-w-lg bg-neutral-900 border border-neutral-700 rounded-2xl shadow-2xl overflow-hidden"
          >
            {/* Tier selector */}
            <div className="px-4 pt-4 pb-3 border-b border-neutral-800">
              <p className="text-xs text-neutral-500 mb-2 font-medium">Tier para agregar</p>
              <div className="flex gap-1.5">
                {TIERS.map((t) => {
                  const colors = TIER_COLORS[t];
                  return (
                    <button
                      key={t}
                      onClick={() => setSelectedTier(t)}
                      className={`flex-1 py-1.5 rounded-lg text-sm font-extrabold transition-all
                        ${selectedTier === t
                          ? `${colors.bg} ${colors.text} ring-2 ring-offset-1 ring-offset-neutral-900 ring-white/30 scale-105`
                          : "bg-neutral-800 text-neutral-500 hover:bg-neutral-700"
                        }`}
                    >
                      {t}
                    </button>
                  );
                })}
              </div>
              <p className="text-xs text-neutral-600 mt-1.5 text-right">
                {TIER_COLORS[selectedTier].label}
              </p>
            </div>

            {/* Search header */}
            <div className="flex items-center gap-3 px-4 py-3 border-b border-neutral-800">
              <svg className="w-4 h-4 text-neutral-400 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M21 21l-4.35-4.35M17 11A6 6 0 1 1 5 11a6 6 0 0 1 12 0z" />
              </svg>
              <input
                ref={inputRef}
                type="search"
                value={q}
                onChange={(e) => handleInput(e.target.value)}
                placeholder="Buscar anime…"
                autoComplete="off"
                className="flex-1 bg-transparent text-white placeholder-neutral-500 outline-none text-sm"
              />
              {loading && (
                <span className="w-4 h-4 rounded-full border-2 border-neutral-500 border-t-transparent animate-spin shrink-0" />
              )}
              <button
                onClick={() => setOpen(false)}
                className="text-neutral-500 hover:text-neutral-300 transition-colors shrink-0 text-lg leading-none"
              >
                ✕
              </button>
            </div>

            {/* Results */}
            <div className="max-h-80 overflow-y-auto">
              {q.trim().length < 2 ? (
                <p className="text-center text-sm text-neutral-600 py-8">
                  Escribí al menos 2 letras para buscar
                </p>
              ) : results.length === 0 && !loading ? (
                <p className="text-center text-sm text-neutral-500 py-8">
                  Sin resultados para &quot;{q}&quot;
                </p>
              ) : (
                results.map((s) => {
                  const isAdded = added.has(s.slug);
                  const isPending = pending === s.slug;
                  const colors = TIER_COLORS[selectedTier];
                  return (
                    <button
                      key={s.slug}
                      onClick={() => handleAdd(s)}
                      disabled={isPending}
                      className="w-full flex items-center gap-3 px-4 py-3 hover:bg-neutral-800 transition-colors disabled:cursor-default"
                    >
                      {/* Cover */}
                      <div className="w-10 h-14 relative shrink-0 rounded overflow-hidden bg-neutral-700">
                        {s.coverUrl && (
                          <Image
                            src={s.coverUrl}
                            alt=""
                            fill
                            sizes="40px"
                            className="object-cover"
                          />
                        )}
                      </div>

                      {/* Info */}
                      <div className="flex-1 min-w-0 text-left">
                        <p className="text-sm text-white truncate">{s.title}</p>
                        <div className="flex gap-1 mt-0.5">
                          {s.type && (
                            <span className="text-[10px] bg-indigo-600/70 text-white px-1.5 py-0.5 rounded">
                              {s.type === "tv" ? "Serie" : s.type === "movie" ? "Película" : s.type.toUpperCase()}
                            </span>
                          )}
                        </div>
                      </div>

                      {/* State */}
                      <div className="shrink-0">
                        {isPending ? (
                          <span className="w-8 h-8 rounded-full border-2 border-indigo-400 border-t-transparent animate-spin inline-block" />
                        ) : isAdded ? (
                          <span className={`px-2 py-1 rounded-md text-xs font-extrabold ${colors.bg} ${colors.text}`}>
                            {selectedTier}
                          </span>
                        ) : (
                          <span className={`px-2 py-1 rounded-md text-xs font-extrabold opacity-40 ${colors.bg} ${colors.text}`}>
                            {selectedTier}
                          </span>
                        )}
                      </div>
                    </button>
                  );
                })
              )}
            </div>

            {/* Footer */}
            <div className="px-4 py-2.5 border-t border-neutral-800 text-xs text-neutral-600">
              Podés cambiar el tier de un anime haciendo click en él · <kbd className="px-1 py-0.5 rounded bg-neutral-800 text-neutral-400 font-mono">Esc</kbd> para cerrar
            </div>
          </div>
        </div>
      )}
    </>
  );
}
