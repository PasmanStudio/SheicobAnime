import Link from "next/link";

interface GenreChipProps {
  name: string;
  href?: string;
  className?: string;
}

/** Chip pill de género — hover cian, sin fondo en reposo. */
export default function GenreChip({ name, href, className = "" }: GenreChipProps) {
  return (
    <Link
      href={href ?? `/genres/${encodeURIComponent(name)}`}
      className={`inline-flex items-center px-3 py-[5px] rounded-full text-xs font-semibold whitespace-nowrap text-ink-2 border border-line-2 hover:text-brand-bright hover:bg-[var(--accent-muted)] hover:border-[var(--accent-border)] transition-all duration-fast ${className}`}
    >
      {name}
    </Link>
  );
}
