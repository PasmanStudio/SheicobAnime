import ScoreBadge from "@/components/ui/ScoreBadge";
import StatusBadge from "@/components/ui/StatusBadge";
import type { Series, SeriesType } from "@/lib/types";
import Image from "next/image";
import Link from "next/link";

const TYPE_LABELS: Record<SeriesType, string> = {
  tv: "Serie",
  movie: "Película",
  ova: "OVA",
  ona: "ONA",
  special: "Especial",
};

interface SeriesCardProps {
  series: Series;
  className?: string;
}

/**
 * Card de poster 2:3 del design system — la info va SOBRE la imagen
 * con protección .sh-protect (gradiente hacia --bg-0), no debajo.
 * Hover: borde claro + lift + zoom sutil de imagen.
 */
export default function SeriesCard({ series, className = "" }: SeriesCardProps) {
  return (
    <Link
      href={`/series/${series.slug}`}
      className={`group block overflow-hidden rounded-card border border-line-1 bg-abyss-2 transition-all duration-fast hover:border-line-2 hover:-translate-y-0.5 hover:shadow-card ${className}`}
    >
      <div className="relative aspect-[2/3] overflow-hidden bg-abyss-3">
        {series.coverUrl ? (
          <Image
            src={series.coverUrl}
            alt={series.title}
            fill
            sizes="(max-width: 640px) 50vw, (max-width: 1024px) 25vw, 16vw"
            className="object-cover transition-transform duration-300 group-hover:scale-[1.04]"
          />
        ) : (
          <div className="flex h-full w-full items-center justify-center px-2 text-center font-display text-4xl italic font-black text-[rgba(255,255,255,0.14)]">
            {series.title.trim()[0]?.toUpperCase() ?? "?"}
          </div>
        )}

        {/* Badges superiores: tipo + score */}
        <div className="absolute inset-x-2 top-2 flex items-start justify-between gap-1.5">
          {series.type ? (
            <span className="rounded-badge bg-[rgba(5,7,11,0.78)] px-[7px] py-[3px] text-[10px] font-bold text-[var(--cyan-300)] backdrop-blur-[4px]">
              {TYPE_LABELS[series.type] ?? series.type}
            </span>
          ) : (
            <span />
          )}
          <ScoreBadge score={series.score} overlay />
        </div>

        {/* Protección + info sobre la imagen */}
        <div className="sh-protect absolute inset-x-0 bottom-0 flex flex-col gap-[5px] px-2.5 pb-2.5 pt-7">
          <h3 className="sh-title line-clamp-2 text-[13px] transition-colors duration-fast group-hover:text-[var(--cyan-200)]">
            {series.title}
          </h3>
          <span className="flex items-center gap-2">
            {series.year && (
              <span className="font-mono text-[10px] tabular-nums text-ink-3">{series.year}</span>
            )}
            {series.status === "ongoing" && <StatusBadge status="ongoing" compact />}
          </span>
        </div>
      </div>
    </Link>
  );
}
