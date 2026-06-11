import ProgressBar from "@/components/ui/ProgressBar";
import Image from "next/image";
import Link from "next/link";

interface EpisodeCardProps {
  href: string;
  seriesTitle: string;
  episodeNumber: number;
  title?: string | null;
  thumbnailUrl?: string | null;
  /** Etiqueta de tiempo relativa (ej: "hace 2 h") o fecha corta */
  timeAgo?: string | null;
  isNew?: boolean;
  /** 0..1 — muestra barra de progreso al pie del thumbnail */
  progress?: number;
  className?: string;
}

/**
 * Card de episodio del design system: thumbnail 16:9 con badge `EP NN` en mono,
 * pill NUEVO con gradiente de acción, play al hover y barra de progreso opcional.
 */
export default function EpisodeCard({
  href,
  seriesTitle,
  episodeNumber,
  title,
  thumbnailUrl,
  timeAgo,
  isNew,
  progress,
  className = "",
}: EpisodeCardProps) {
  return (
    <Link
      href={href}
      className={`group block overflow-hidden rounded-card border border-line-1 bg-abyss-2 transition-all duration-fast hover:border-line-2 hover:-translate-y-0.5 hover:shadow-card ${className}`}
    >
      <div className="relative aspect-video overflow-hidden bg-abyss-3">
        {thumbnailUrl ? (
          <Image
            src={thumbnailUrl}
            alt={seriesTitle}
            fill
            sizes="(max-width: 640px) 50vw, (max-width: 1024px) 33vw, 240px"
            className="object-cover transition-transform duration-300 group-hover:scale-[1.04]"
          />
        ) : (
          <div className="flex h-full w-full items-center justify-center font-display text-3xl italic font-black text-[rgba(255,255,255,0.14)]">
            {seriesTitle.trim()[0]?.toUpperCase() ?? "?"}
          </div>
        )}

        {/* Badge EP NN — mono, tabular */}
        <span className="absolute left-2 top-2 rounded-badge bg-[rgba(5,7,11,0.78)] px-2 py-[3px] font-mono text-[11px] font-bold tabular-nums text-[var(--cyan-300)] backdrop-blur-[4px]">
          EP {String(episodeNumber).padStart(2, "0")}
        </span>

        {isNew && (
          <span
            className="absolute right-2 top-2 rounded-badge px-2 py-[3px] text-[10px] font-extrabold text-[var(--text-on-accent)]"
            style={{ background: "var(--grad-action)" }}
          >
            NUEVO
          </span>
        )}

        {/* Play al hover */}
        <span className="absolute inset-0 flex items-center justify-center bg-[rgba(5,7,11,0.35)] opacity-0 transition-opacity duration-fast group-hover:opacity-100">
          <span
            className="flex h-11 w-11 items-center justify-center rounded-full pl-[3px] text-[var(--text-on-accent)] shadow-glow"
            style={{ background: "var(--grad-action)" }}
          >
            <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden>
              <path d="M6 4.5v15l13-7.5-13-7.5z" />
            </svg>
          </span>
        </span>

        {typeof progress === "number" && progress > 0 && (
          <span className="absolute inset-x-0 bottom-0">
            <ProgressBar value={progress} height={4} className="!rounded-none" />
          </span>
        )}
      </div>

      <div className="flex flex-col gap-[3px] px-3 pb-3 pt-2.5">
        <span className="sh-title truncate text-[13px]">{seriesTitle}</span>
        <span className="flex justify-between gap-2">
          <span className="truncate text-xs text-ink-2">
            {title || `Episodio ${episodeNumber}`}
          </span>
          {timeAgo && (
            <span className="whitespace-nowrap font-mono text-[10px] tabular-nums text-ink-3">
              {timeAgo}
            </span>
          )}
        </span>
      </div>
    </Link>
  );
}
