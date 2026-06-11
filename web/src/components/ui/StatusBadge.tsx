import type { SeriesStatus } from "@/lib/types";

const STATUS_MAP: Record<
  SeriesStatus,
  { label: string; color: string; bg: string; border: string; dot?: boolean }
> = {
  ongoing: {
    label: "En emisión",
    color: "var(--success)",
    bg: "rgba(74,222,140,0.12)",
    border: "rgba(74,222,140,0.3)",
    dot: true,
  },
  completed: {
    label: "Concluido",
    color: "var(--cyan-300)",
    bg: "var(--accent-muted)",
    border: "var(--accent-border)",
  },
  upcoming: {
    label: "Por estrenar",
    color: "var(--warning)",
    bg: "rgba(255,197,61,0.12)",
    border: "rgba(255,197,61,0.3)",
  },
  hiatus: {
    label: "En pausa",
    color: "var(--text-2)",
    bg: "var(--bg-3)",
    border: "var(--border-2)",
  },
};

interface StatusBadgeProps {
  status: SeriesStatus | null | undefined;
  /** Versión compacta para overlays de poster */
  compact?: boolean;
  className?: string;
}

export default function StatusBadge({ status, compact, className = "" }: StatusBadgeProps) {
  const s = status ? STATUS_MAP[status] : undefined;
  if (!s) return null;
  return (
    <span
      className={`inline-flex items-center gap-1.5 rounded-badge font-bold whitespace-nowrap ${
        compact ? "px-1.5 py-px text-[9px]" : "px-2 py-[3px] text-[11px]"
      } ${className}`}
      style={{ color: s.color, background: s.bg, border: `1px solid ${s.border}` }}
    >
      {s.dot && <span className="sh-live-dot !w-1.5 !h-1.5" />}
      {s.label}
    </span>
  );
}
