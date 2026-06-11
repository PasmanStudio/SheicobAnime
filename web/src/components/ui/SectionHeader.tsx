import Link from "next/link";

interface SectionHeaderProps {
  title: string;
  /** Label mono uppercase arriba del título (ej: "Actualizado hace 12 min") */
  eyebrow?: string;
  /** Texto del link de acción a la derecha (ej: "Ver todo") */
  action?: string;
  actionHref?: string;
  size?: "md" | "lg";
  className?: string;
}

/**
 * Section header del design system: corte de 14° + Archivo italic uppercase.
 * Reemplaza los headers con emoji (📅🏆) del sitio viejo.
 */
export default function SectionHeader({
  title,
  eyebrow,
  action,
  actionHref,
  size = "md",
  className = "",
}: SectionHeaderProps) {
  return (
    <div className={`flex items-end justify-between gap-4 ${className}`}>
      <div className="flex flex-col gap-1">
        {eyebrow && <span className="sh-label">{eyebrow}</span>}
        <span className="sh-section-header items-center">
          <span className="sh-cut" />
          <h2
            className="sh-display"
            style={{
              fontSize:
                size === "lg"
                  ? "clamp(22px, 3vw, 28px)"
                  : "clamp(17px, 2.4vw, 21px)",
            }}
          >
            {title}
          </h2>
        </span>
      </div>
      {action && actionHref && (
        <Link
          href={actionHref}
          className="pb-0.5 text-[13px] font-semibold text-brand-bright whitespace-nowrap hover:text-[var(--cyan-200)] transition-colors duration-fast"
        >
          {action} →
        </Link>
      )}
    </div>
  );
}
