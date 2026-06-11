interface ScoreBadgeProps {
  score: number | null | undefined;
  /** Sobre imagen: fondo oscuro translúcido sin borde */
  overlay?: boolean;
  size?: "sm" | "lg";
  className?: string;
}

/**
 * Score en mono con la semántica del sitio: ≥8 verde / ≥6 dorado / resto gris.
 */
export default function ScoreBadge({ score, overlay, size = "sm", className = "" }: ScoreBadgeProps) {
  if (score === null || score === undefined) return null;
  const color =
    score >= 8 ? "var(--score-high)" : score >= 6 ? "var(--score-mid)" : "var(--score-low)";
  return (
    <span
      className={`inline-flex items-center gap-1 rounded-badge font-mono font-bold tabular-nums ${
        size === "lg" ? "px-2.5 py-[5px] text-[15px]" : "px-[7px] py-[3px] text-[11px]"
      } ${
        overlay
          ? "bg-[rgba(5,7,11,0.78)] backdrop-blur-[4px]"
          : "bg-abyss-3 border border-line-2"
      } ${className}`}
      style={{ color }}
    >
      ★ {Number(score).toFixed(1)}
    </span>
  );
}
