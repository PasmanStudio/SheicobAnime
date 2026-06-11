interface ProgressBarProps {
  /** 0..1 */
  value: number;
  height?: number;
  className?: string;
}

/** Barra de progreso con el gradiente de acción (cian). */
export default function ProgressBar({ value, height = 4, className = "" }: ProgressBarProps) {
  const pct = Math.max(0, Math.min(1, value)) * 100;
  return (
    <div
      className={`w-full overflow-hidden rounded-full bg-[rgba(255,255,255,0.14)] ${className}`}
      style={{ height }}
    >
      <div
        className="h-full rounded-full transition-[width] duration-base"
        style={{ width: `${pct}%`, background: "var(--grad-action)" }}
      />
    </div>
  );
}
