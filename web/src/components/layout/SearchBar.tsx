"use client";

import type { SeriesSuggest } from "@/lib/types";
import Image from "next/image";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";

const RECENT_KEY = "sheicob_recent_searches";
const MAX_RECENT = 6;

function getRecentSearches(): string[] {
  if (typeof window === "undefined") return [];
  try {
    const raw = localStorage.getItem(RECENT_KEY);
    return raw ? (JSON.parse(raw) as string[]).slice(0, MAX_RECENT) : [];
  } catch {
    return [];
  }
}

function saveRecentSearch(q: string) {
  const recent = getRecentSearches().filter((s) => s !== q);
  recent.unshift(q);
  localStorage.setItem(RECENT_KEY, JSON.stringify(recent.slice(0, MAX_RECENT)));
}

const API_BASE_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000";

export default function SearchBar() {
  const router = useRouter();
  const [q, setQ] = useState("");
  const [suggestions, setSuggestions] = useState<SeriesSuggest[]>([]);
  const [recentSearches, setRecentSearches] = useState<string[]>([]);
  const [isOpen, setIsOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(-1);
  const containerRef = useRef<HTMLDivElement>(null);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>();

  useEffect(() => {
    setRecentSearches(getRecentSearches());
  }, []);

  // Close dropdown on outside click
  useEffect(() => {
    function handleClickOutside(e: MouseEvent) {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setIsOpen(false);
      }
    }
    document.addEventListener("mousedown", handleClickOutside);
    return () => document.removeEventListener("mousedown", handleClickOutside);
  }, []);

  const fetchSuggestions = useCallback(async (term: string) => {
    if (term.length < 2) {
      setSuggestions([]);
      return;
    }
    try {
      const res = await fetch(
        `${API_BASE_URL}/series/suggest?q=${encodeURIComponent(term)}`,
        { cache: "no-store" }
      );
      if (res.ok) {
        const data = (await res.json()) as SeriesSuggest[];
        setSuggestions(data);
      }
    } catch {
      // silently fail
    }
  }, []);

  function handleInputChange(value: string) {
    setQ(value);
    setActiveIndex(-1);

    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => fetchSuggestions(value.trim()), 250);

    if (value.trim().length > 0 || recentSearches.length > 0) {
      setIsOpen(true);
    }
  }

  function handleSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const trimmed = q.trim();
    if (trimmed) {
      saveRecentSearch(trimmed);
      setRecentSearches(getRecentSearches());
      setIsOpen(false);
      router.push(`/search?q=${encodeURIComponent(trimmed)}`);
    }
  }

  function handleSelectSuggestion(slug: string) {
    setIsOpen(false);
    router.push(`/series/${slug}`);
  }

  function handleSelectRecent(term: string) {
    setQ(term);
    saveRecentSearch(term);
    setIsOpen(false);
    router.push(`/search?q=${encodeURIComponent(term)}`);
  }

  function handleClearRecent() {
    localStorage.removeItem(RECENT_KEY);
    setRecentSearches([]);
  }

  function handleKeyDown(e: React.KeyboardEvent) {
    const totalItems = suggestions.length > 0 ? suggestions.length : recentSearches.length;
    if (!isOpen || totalItems === 0) return;

    if (e.key === "ArrowDown") {
      e.preventDefault();
      setActiveIndex((prev) => (prev + 1) % totalItems);
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setActiveIndex((prev) => (prev - 1 + totalItems) % totalItems);
    } else if (e.key === "Enter" && activeIndex >= 0) {
      e.preventDefault();
      if (suggestions.length > 0) {
        handleSelectSuggestion(suggestions[activeIndex].slug);
      } else if (recentSearches.length > 0) {
        handleSelectRecent(recentSearches[activeIndex]);
      }
    } else if (e.key === "Escape") {
      setIsOpen(false);
    }
  }

  const showSuggestions = isOpen && suggestions.length > 0;
  const showRecent = isOpen && suggestions.length === 0 && q.trim().length === 0 && recentSearches.length > 0;

  return (
    <div ref={containerRef} className="relative">
      <form onSubmit={handleSubmit} className="flex items-center gap-1.5">
        <input
          type="search"
          value={q}
          onChange={(e) => handleInputChange(e.target.value)}
          onFocus={() => {
            if (q.trim().length > 0 || recentSearches.length > 0) setIsOpen(true);
          }}
          onKeyDown={handleKeyDown}
          placeholder="Buscar anime…"
          aria-label="Buscar anime"
          autoComplete="off"
          className="w-40 sm:w-64 rounded-md bg-neutral-800 border border-neutral-700 px-3 py-1.5 text-sm text-white placeholder-neutral-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
        />
        <button
          type="submit"
          className="shrink-0 rounded-md bg-indigo-600 hover:bg-indigo-700 px-3 py-1.5 text-sm font-medium text-white transition-colors"
        >
          Buscar
        </button>
      </form>

      {/* Suggestions dropdown */}
      {showSuggestions && (
        <div className="absolute right-0 top-full mt-1 w-80 bg-neutral-900 border border-neutral-700 rounded-lg shadow-xl z-50 overflow-hidden">
          {suggestions.map((item, i) => (
            <button
              key={item.slug}
              type="button"
              onClick={() => handleSelectSuggestion(item.slug)}
              className={`w-full flex items-center gap-3 px-3 py-2 text-left hover:bg-neutral-800 transition-colors ${
                i === activeIndex ? "bg-neutral-800" : ""
              }`}
            >
              <div className="w-8 h-11 relative shrink-0 rounded overflow-hidden bg-neutral-700">
                {item.coverUrl && (
                  <Image
                    src={item.coverUrl}
                    alt=""
                    fill
                    sizes="32px"
                    className="object-cover"
                  />
                )}
              </div>
              <div className="min-w-0 flex-1">
                <p className="text-sm text-white truncate">{item.title}</p>
                <div className="flex gap-1 mt-0.5">
                  {item.type && (
                    <span className="text-[10px] bg-indigo-600/80 text-white px-1 rounded">
                      {item.type === "tv" ? "Serie" : item.type === "movie" ? "Película" : item.type.toUpperCase()}
                    </span>
                  )}
                  {item.status && (
                    <span className={`text-[10px] px-1 rounded text-white ${
                      item.status === "ongoing" ? "bg-green-600/80" :
                      item.status === "completed" ? "bg-blue-600/80" :
                      item.status === "upcoming" ? "bg-amber-600/80" : "bg-neutral-600/80"
                    }`}>
                      {item.status === "ongoing" ? "En emisión" :
                       item.status === "completed" ? "Concluido" :
                       item.status === "upcoming" ? "Por estrenar" : item.status}
                    </span>
                  )}
                </div>
              </div>
            </button>
          ))}
        </div>
      )}

      {/* Recent searches dropdown */}
      {showRecent && (
        <div className="absolute right-0 top-full mt-1 w-72 bg-neutral-900 border border-neutral-700 rounded-lg shadow-xl z-50 overflow-hidden">
          <div className="flex items-center justify-between px-3 py-2 border-b border-neutral-800">
            <span className="text-xs text-neutral-400 font-medium">Búsquedas recientes</span>
            <button
              type="button"
              onClick={handleClearRecent}
              className="text-xs text-indigo-400 hover:text-indigo-300"
            >
              Limpiar
            </button>
          </div>
          {recentSearches.map((term, i) => (
            <button
              key={term}
              type="button"
              onClick={() => handleSelectRecent(term)}
              className={`w-full flex items-center gap-2 px-3 py-2 text-left hover:bg-neutral-800 transition-colors ${
                i === activeIndex ? "bg-neutral-800" : ""
              }`}
            >
              <svg className="w-3.5 h-3.5 text-neutral-500 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
              <span className="text-sm text-neutral-300 truncate">{term}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
