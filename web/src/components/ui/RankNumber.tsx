interface RankNumberProps {
  rank: number;
  size?: "sm" | "md" | "lg";
  className?: string;
  style?: React.CSSProperties;
}

/**
 * Número de ranking en Archivo italic: #1 dorado, #2 plata, #3 bronce, resto gris.
 */
export default function RankNumber({ rank, size = "md", className = "", style }: RankNumberProps) {
  const color =
    rank === 1
      ? "var(--gold)"
      : rank === 2
        ? "var(--silver)"
        : rank === 3
          ? "var(--bronze)"
          : "var(--text-3)";
  const fontSize = size === "lg" ? 30 : size === "md" ? 18 : 14;
  return (
    <span
      className={`font-display font-extrabold italic tabular-nums leading-none text-center shrink-0 ${className}`}
      style={{
        fontSize,
        color,
        minWidth: size === "lg" ? 52 : 34,
        ...style,
      }}
    >
      #{rank}
    </span>
  );
}
