const TIER_COLORS: Record<string, string> = {
  S: "var(--tier-s)",
  A: "var(--tier-a)",
  B: "var(--tier-b)",
  C: "var(--tier-c)",
  D: "var(--tier-d)",
};

interface TierBadgeProps {
  tier: string | null | undefined;
  size?: number;
  className?: string;
}

/**
 * Paralelogramo de 14° con la letra del tier — el corte hecho badge.
 * Escala de calor clásica: S rojo → D azul.
 */
export default function TierBadge({ tier, size = 28, className = "" }: TierBadgeProps) {
  const color = (tier && TIER_COLORS[tier.toUpperCase()]) || "var(--tier-none)";
  return (
    <span
      className={`inline-flex items-center justify-center shrink-0 rounded ${className}`}
      style={{ width: size, height: size, background: color, transform: "skewX(-14deg)" }}
    >
      <span
        className="font-display font-black italic leading-none"
        style={{
          transform: "skewX(14deg)",
          fontSize: Math.round(size * 0.55),
          color: "#10131B",
        }}
      >
        {tier ? tier.toUpperCase() : "—"}
      </span>
    </span>
  );
}
