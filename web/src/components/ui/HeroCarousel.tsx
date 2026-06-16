"use client";

import ScoreBadge from "@/components/ui/ScoreBadge";
import StatusBadge from "@/components/ui/StatusBadge";
import { TYPE_LABELS } from "@/lib/labels";
import type { Series } from "@/lib/types";
import Image from "next/image";
import Link from "next/link";
import { useCallback, useEffect, useState } from "react";

interface HeroCarouselProps {
  series: Series[];
}

export default function HeroCarousel({ series }: HeroCarouselProps) {
  const [current, setCurrent] = useState(0);
  const [paused, setPaused] = useState(false);
  const [reduceMotion, setReduceMotion] = useState(false);
  const items = series.slice(0, 10);

  const next = useCallback(() => {
    setCurrent((prev) => (prev + 1) % items.length);
  }, [items.length]);

  const prev = useCallback(() => {
    setCurrent((prev) => (prev - 1 + items.length) % items.length);
  }, [items.length]);

  // Respetar prefers-reduced-motion para el auto-avance
  useEffect(() => {
    const mq = window.matchMedia("(prefers-reduced-motion: reduce)");
    setReduceMotion(mq.matches);
    const onChange = (e: MediaQueryListEvent) => setReduceMotion(e.matches);
    mq.addEventListener("change", onChange);
    return () => mq.removeEventListener("change", onChange);
  }, []);

  // Auto-advance every 6 seconds — pausado en hover/foco y con reduced-motion
  useEffect(() => {
    if (items.length <= 1 || paused || reduceMotion) return;
    const interval = setInterval(next, 6000);
    return () => clearInterval(interval);
  }, [next, items.length, paused, reduceMotion]);

  if (items.length === 0) return null;

  const item = items[current];

  return (
    <div
      className="relative w-full h-[300px] sm:h-[400px] md:h-[480px] overflow-hidden rounded-modal border border-line-1 bg-abyss-1"
      role="region"
      aria-roledescription="carrusel"
      aria-label="Series destacadas"
      onMouseEnter={() => setPaused(true)}
      onMouseLeave={() => setPaused(false)}
      onFocus={() => setPaused(true)}
      onBlur={() => setPaused(false)}
    >
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
          <div className="w-full h-full bg-abyss-2" />
        )}
        {/* Protección hacia el abismo — nunca negro puro */}
        <div
          className="absolute inset-0"
          style={{
            background:
              "linear-gradient(to right, rgba(7,9,14,0.92) 0%, rgba(7,9,14,0.6) 55%, rgba(7,9,14,0.15) 100%)",
          }}
        />
        <div
          className="absolute inset-0"
          style={{
            background:
              "linear-gradient(to top, rgba(7,9,14,0.88) 0%, transparent 55%)",
          }}
        />
        {/* Speed-lines — textura de marca, una por pantalla */}
        <div className="sh-speedlines absolute inset-0" />
      </div>

      {/* Content */}
      <div className="relative h-full flex flex-col justify-end gap-3 pl-14 pr-14 pb-6 sm:pl-16 sm:pr-6 sm:pb-8 md:pl-10 md:pr-10 md:pb-10 max-w-3xl">
        {/* Eyebrow mono */}
        <span className="sh-label flex items-center gap-2">
          {item.status === "ongoing" && <span className="sh-live-dot" />}
          {item.status === "ongoing" ? "En emisión" : (item.type && TYPE_LABELS[item.type]) || "Destacado"}
          {item.year ? ` · ${item.year}` : ""}
        </span>

        <h2 className="sh-display !text-[clamp(24px,4vw,40px)] drop-shadow-lg m-0">
          {item.title}
        </h2>

        {/* Badges */}
        <div className="flex items-center gap-2.5 flex-wrap">
          <ScoreBadge score={item.score} size="lg" />
          {item.status && item.status !== "ongoing" && <StatusBadge status={item.status} />}
          {item.episodeCount ? (
            <span className="sh-stat text-[13px] text-ink-2">
              {item.episodeCount} episodios
            </span>
          ) : null}
          {item.studio && <span className="text-[13px] text-ink-2">{item.studio}</span>}
        </div>

        {/* Synopsis (truncated) */}
        {item.synopsis && (
          <p className="sh-body text-sm line-clamp-2 sm:line-clamp-3 max-w-xl m-0">
            {item.synopsis}
          </p>
        )}

        {/* Action buttons */}
        <div className="flex flex-wrap items-center gap-2.5 mt-1">
          <Link
            href={`/series/${item.slug}`}
            className="inline-flex items-center gap-2 h-11 px-5 rounded-btn text-[15px] font-bold text-[var(--text-on-accent)] shadow-glow transition-all duration-fast hover:brightness-110 active:scale-[0.97]"
            style={{ background: "var(--grad-action)" }}
          >
            <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 24 24" aria-hidden>
              <path d="M6 4.5v15l13-7.5-13-7.5z" />
            </svg>
            Ver ahora
          </Link>
          <Link
            href={`/series/${item.slug}`}
            className="inline-flex items-center gap-2 h-11 px-5 rounded-btn bg-abyss-3 border border-line-2 text-[15px] font-semibold text-ink-1 transition-all duration-fast hover:bg-[var(--bg-3)] hover:border-[var(--border-2)] hover:brightness-110 active:scale-[0.97]"
          >
            Detalles
          </Link>
        </div>
      </div>

      {/* Navigation arrows — vertically centered, accessible on mobile */}
      {items.length > 1 && (
        <>
          <button
            type="button"
            onClick={prev}
            className="absolute left-2 top-1/2 -translate-y-1/2 w-10 h-10 flex items-center justify-center bg-[var(--bg-overlay)] hover:bg-abyss-3 border border-line-1 text-ink-1 rounded-full transition-colors duration-fast"
            aria-label="Anterior"
          >
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
              <path d="m15 18-6-6 6-6" />
            </svg>
          </button>
          <button
            type="button"
            onClick={next}
            className="absolute right-2 top-1/2 -translate-y-1/2 w-10 h-10 flex items-center justify-center bg-[var(--bg-overlay)] hover:bg-abyss-3 border border-line-1 text-ink-1 rounded-full transition-colors duration-fast"
            aria-label="Siguiente"
          >
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
              <path d="m9 18 6-6-6-6" />
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
              className={`h-2 rounded-full transition-all duration-fast ${
                i === current ? "bg-brand-bright w-4" : "bg-[rgba(255,255,255,0.35)] w-2 hover:bg-[rgba(255,255,255,0.6)]"
              }`}
              aria-label={`Ir a slide ${i + 1}`}
            />
          ))}
        </div>
      )}
    </div>
  );
}
