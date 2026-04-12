"use client";

import type { Series, SeriesStatus, SeriesType } from "@/lib/types";
import Image from "next/image";
import Link from "next/link";
import { useCallback, useEffect, useState } from "react";

const TYPE_LABELS: Record<SeriesType, string> = {
  tv: "Serie",
  movie: "Película",
  ova: "OVA",
  ona: "ONA",
  special: "Especial",
};

const STATUS_LABELS: Record<SeriesStatus, string> = {
  ongoing: "En emisión",
  completed: "Concluido",
  upcoming: "Por estrenar",
  hiatus: "En pausa",
};

interface HeroCarouselProps {
  series: Series[];
}

export default function HeroCarousel({ series }: HeroCarouselProps) {
  const [current, setCurrent] = useState(0);
  const items = series.slice(0, 10);

  const next = useCallback(() => {
    setCurrent((prev) => (prev + 1) % items.length);
  }, [items.length]);

  const prev = useCallback(() => {
    setCurrent((prev) => (prev - 1 + items.length) % items.length);
  }, [items.length]);

  // Auto-advance every 6 seconds
  useEffect(() => {
    if (items.length <= 1) return;
    const interval = setInterval(next, 6000);
    return () => clearInterval(interval);
  }, [next, items.length]);

  if (items.length === 0) return null;

  const item = items[current];

  return (
    <div className="relative w-full h-[300px] sm:h-[400px] md:h-[480px] overflow-hidden rounded-xl bg-neutral-900">
      {/* Background image */}
      <div className="absolute inset-0">
        {(item.bannerUrl ?? item.coverUrl) ? (
          <Image
            src={item.bannerUrl ?? item.coverUrl!}
            alt=""
            fill
            sizes="100vw"
            className="object-cover"
            priority={current === 0}
          />
        ) : (
          <div className="w-full h-full bg-gradient-to-br from-indigo-900 to-neutral-900" />
        )}
        {/* Gradient overlays */}
        <div className="absolute inset-0 bg-gradient-to-r from-black/90 via-black/60 to-transparent" />
        <div className="absolute inset-0 bg-gradient-to-t from-black/80 via-transparent to-transparent" />
      </div>

      {/* Content */}
      <div className="relative h-full flex flex-col justify-end p-6 sm:p-8 md:p-10 max-w-3xl">
        <h2 className="text-2xl sm:text-3xl md:text-4xl font-bold text-white leading-tight drop-shadow-lg">
          {item.title}
        </h2>

        {/* Badges */}
        <div className="flex items-center gap-2 mt-3">
          {item.type && (
            <span className="inline-flex items-center gap-1 bg-neutral-800/80 text-white text-xs font-medium px-2.5 py-1 rounded">
              <svg className="w-3 h-3" fill="currentColor" viewBox="0 0 20 20">
                <path d="M4 3a2 2 0 00-2 2v10a2 2 0 002 2h12a2 2 0 002-2V5a2 2 0 00-2-2H4z" />
              </svg>
              {TYPE_LABELS[item.type] ?? item.type}
            </span>
          )}
          {item.status && (
            <span className="inline-flex items-center gap-1 bg-neutral-800/80 text-white text-xs font-medium px-2.5 py-1 rounded">
              <svg className="w-3 h-3" fill="currentColor" viewBox="0 0 20 20">
                <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm1-12a1 1 0 10-2 0v4a1 1 0 00.293.707l2.828 2.829a1 1 0 101.415-1.415L11 9.586V6z" clipRule="evenodd" />
              </svg>
              {STATUS_LABELS[item.status] ?? item.status}
            </span>
          )}
          {item.year && (
            <span className="bg-neutral-800/80 text-neutral-300 text-xs px-2.5 py-1 rounded">
              {item.year}
            </span>
          )}
        </div>

        {/* Synopsis (truncated) */}
        {item.synopsis && (
          <p className="text-sm text-neutral-300 mt-3 line-clamp-2 sm:line-clamp-3 max-w-xl">
            {item.synopsis}
          </p>
        )}

        {/* Action buttons */}
        <div className="flex items-center gap-3 mt-4">
          <Link
            href={`/series/${item.slug}`}
            className="inline-flex items-center gap-2 bg-neutral-700/80 hover:bg-neutral-600 text-white text-sm font-medium px-5 py-2.5 rounded-lg transition-colors"
          >
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            Detalles
          </Link>
          <Link
            href={`/series/${item.slug}`}
            className="inline-flex items-center gap-2 bg-indigo-600 hover:bg-indigo-700 text-white text-sm font-medium px-5 py-2.5 rounded-lg transition-colors"
          >
            <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 20 20">
              <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM9.555 7.168A1 1 0 008 8v4a1 1 0 001.555.832l3-2a1 1 0 000-1.664l-3-2z" clipRule="evenodd" />
            </svg>
            Ver Ahora
          </Link>
        </div>
      </div>

      {/* Navigation arrows */}
      {items.length > 1 && (
        <>
          <button
            type="button"
            onClick={prev}
            className="absolute right-14 top-4 sm:top-6 w-9 h-9 flex items-center justify-center bg-neutral-800/70 hover:bg-neutral-700 text-white rounded-lg transition-colors"
            aria-label="Anterior"
          >
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 19l-7-7 7-7" />
            </svg>
          </button>
          <button
            type="button"
            onClick={next}
            className="absolute right-4 top-4 sm:top-6 w-9 h-9 flex items-center justify-center bg-neutral-800/70 hover:bg-neutral-700 text-white rounded-lg transition-colors"
            aria-label="Siguiente"
          >
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
            </svg>
          </button>
        </>
      )}

      {/* Dots indicator */}
      {items.length > 1 && (
        <div className="absolute bottom-3 left-1/2 -translate-x-1/2 flex gap-1.5">
          {items.map((_, i) => (
            <button
              key={i}
              type="button"
              onClick={() => setCurrent(i)}
              className={`w-2 h-2 rounded-full transition-all ${
                i === current ? "bg-white w-4" : "bg-white/40 hover:bg-white/60"
              }`}
              aria-label={`Ir a slide ${i + 1}`}
            />
          ))}
        </div>
      )}
    </div>
  );
}
