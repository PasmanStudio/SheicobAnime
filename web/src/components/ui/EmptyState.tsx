import Link from "next/link";
import type { ReactNode } from "react";

interface EmptyStateProps {
  title: string;
  description?: ReactNode;
  /** CTA opcional para que el estado se sienta intencional, no un callejón sin salida. */
  cta?: { href: string; label: string };
}

/**
 * Estado vacío de marca: el corte + copy + CTA opcional.
 * Unifica el look de "sin resultados" / "sin episodios" en todo el sitio.
 */
export default function EmptyState({ title, description, cta }: Readonly<EmptyStateProps>) {
  return (
    <div className="flex flex-col items-center gap-3 rounded-card border border-line-1 bg-abyss-2 px-6 py-12 text-center">
      <span className="sh-section-header items-center justify-center">
        <span className="sh-cut" />
        <h3 className="sh-title text-[15px] m-0">{title}</h3>
      </span>
      {description && (
        <p className="sh-body text-sm text-ink-3 max-w-sm m-0">{description}</p>
      )}
      {cta && (
        <Link
          href={cta.href}
          className="mt-1 inline-flex items-center gap-2 h-10 px-4 rounded-btn text-[14px] font-bold text-[var(--text-on-accent)] transition-all duration-fast hover:brightness-110 active:scale-[0.97]"
          style={{ background: "var(--grad-action)" }}
        >
          {cta.label}
        </Link>
      )}
    </div>
  );
}
