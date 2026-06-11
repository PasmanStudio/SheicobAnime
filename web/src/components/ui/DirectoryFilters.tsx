"use client";

import type { SeriesStatus, SeriesType } from "@/lib/types";
import { useRouter, useSearchParams } from "next/navigation";
import { useCallback } from "react";

const GENRES = [
  "Accion", "Aventura", "Comedia", "Drama", "Ecchi", "Fantasia", "Harem",
  "Historico", "Isekai", "Josei", "Magia", "Mecha", "Militar", "Misterio",
  "Musica", "Parodia", "Policial", "Psicologico", "Romance", "Sci-Fi",
  "Seinen", "Shoujo", "Shounen", "Sobrenatural", "Deportes", "Terror",
  "Thriller", "Vampiros", "Yaoi", "Yuri",
];

const LETTERS = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".split("");

const TYPES: { value: SeriesType; label: string }[] = [
  { value: "tv", label: "Series" },
  { value: "movie", label: "Películas" },
  { value: "special", label: "Especiales" },
  { value: "ova", label: "OVAs" },
  { value: "ona", label: "ONAs" },
];

const STATUSES: { value: SeriesStatus; label: string }[] = [
  { value: "ongoing", label: "En emisión" },
  { value: "completed", label: "Finalizado" },
  { value: "upcoming", label: "Por estrenar" },
];

const YEARS = Array.from({ length: 47 }, (_, i) => 2026 - i);

const SORT_OPTIONS = [
  { value: "updated", label: "Por fecha" },
  { value: "title", label: "Por nombre A-Z" },
  { value: "title_desc", label: "Por nombre Z-A" },
  { value: "score", label: "Por popularidad" },
  { value: "year", label: "Por año" },
];

interface DirectoryFiltersProps {
  currentFilters: {
    genre?: string;
    letter?: string;
    type?: string;
    status?: string;
    year?: string;
    sort?: string;
  };
}

export default function DirectoryFilters({ currentFilters }: DirectoryFiltersProps) {
  const router = useRouter();
  const searchParams = useSearchParams();

  const updateFilter = useCallback(
    (key: string, value: string) => {
      const params = new URLSearchParams(searchParams.toString());
      if (value) {
        params.set(key, value);
      } else {
        params.delete(key);
      }
      // Reset to page 1 when filters change
      params.delete("page");
      router.push(`/directory?${params.toString()}`);
    },
    [router, searchParams]
  );

  return (
    <div className="space-y-4">
      {/* Filter selects grid */}
      <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-6 gap-3">
        {/* Sort */}
        <select
          value={currentFilters.sort ?? "updated"}
          onChange={(e) => updateFilter("sort", e.target.value)}
          className="bg-abyss-1 border border-line-1 text-ink-1 text-sm rounded-btn px-3 py-2 outline-none focus:border-brand focus:shadow-focus transition-all duration-fast"
        >
          <option value="">Ordenar por</option>
          {SORT_OPTIONS.map((o) => (
            <option key={o.value} value={o.value}>{o.label}</option>
          ))}
        </select>

        {/* Genre */}
        <select
          value={currentFilters.genre ?? ""}
          onChange={(e) => updateFilter("genre", e.target.value)}
          className="bg-abyss-1 border border-line-1 text-ink-1 text-sm rounded-btn px-3 py-2 outline-none focus:border-brand focus:shadow-focus transition-all duration-fast"
        >
          <option value="">Género</option>
          {GENRES.map((g) => (
            <option key={g} value={g}>{g}</option>
          ))}
        </select>

        {/* Type */}
        <select
          value={currentFilters.type ?? ""}
          onChange={(e) => updateFilter("type", e.target.value)}
          className="bg-abyss-1 border border-line-1 text-ink-1 text-sm rounded-btn px-3 py-2 outline-none focus:border-brand focus:shadow-focus transition-all duration-fast"
        >
          <option value="">Tipo</option>
          {TYPES.map((t) => (
            <option key={t.value} value={t.value}>{t.label}</option>
          ))}
        </select>

        {/* Status */}
        <select
          value={currentFilters.status ?? ""}
          onChange={(e) => updateFilter("status", e.target.value)}
          className="bg-abyss-1 border border-line-1 text-ink-1 text-sm rounded-btn px-3 py-2 outline-none focus:border-brand focus:shadow-focus transition-all duration-fast"
        >
          <option value="">Estado</option>
          {STATUSES.map((s) => (
            <option key={s.value} value={s.value}>{s.label}</option>
          ))}
        </select>

        {/* Year */}
        <select
          value={currentFilters.year ?? ""}
          onChange={(e) => updateFilter("year", e.target.value)}
          className="bg-abyss-1 border border-line-1 text-ink-1 text-sm rounded-btn px-3 py-2 outline-none focus:border-brand focus:shadow-focus transition-all duration-fast"
        >
          <option value="">Año</option>
          {YEARS.map((y) => (
            <option key={y} value={y}>{y}</option>
          ))}
        </select>

        {/* Clear button */}
        <button
          type="button"
          onClick={() => router.push("/directory")}
          className="bg-abyss-3 border border-line-2 hover:brightness-110 text-ink-1 text-sm font-semibold rounded-btn px-3 py-2 transition-all duration-fast"
        >
          Limpiar filtros
        </button>
      </div>

      {/* Letter filter — horizontally scrollable on mobile */}
      <div className="flex gap-1 overflow-x-auto pb-1 scrollbar-thin scrollbar-track-transparent scrollbar-thumb-neutral-700">
        <button
          type="button"
          onClick={() => updateFilter("letter", "")}
          className={`shrink-0 px-3 py-1.5 text-xs rounded-badge font-mono font-semibold transition-colors duration-fast ${
            !currentFilters.letter
              ? "bg-[var(--accent-muted)] text-brand-bright border border-[var(--accent-border)]"
              : "bg-abyss-2 text-ink-3 border border-line-1 hover:text-ink-1 hover:border-line-2"
          }`}
        >
          Todos
        </button>
        {LETTERS.map((l) => (
          <button
            key={l}
            type="button"
            onClick={() => updateFilter("letter", l)}
            className={`shrink-0 px-2.5 py-1.5 text-xs rounded-badge font-mono font-semibold transition-colors duration-fast ${
              currentFilters.letter === l
                ? "bg-[var(--accent-muted)] text-brand-bright border border-[var(--accent-border)]"
                : "bg-abyss-2 text-ink-3 border border-line-1 hover:text-ink-1 hover:border-line-2"
            }`}
          >
            {l}
          </button>
        ))}
      </div>
    </div>
  );
}
