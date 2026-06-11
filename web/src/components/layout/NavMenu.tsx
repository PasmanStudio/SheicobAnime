"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";

const NAV_LINKS = [
  { href: "/", label: "Inicio" },
  { href: "/temporada", label: "Temporada" },
  { href: "/directory", label: "Directorio" },
  { href: "/ranking", label: "Ranking" },
  { href: "/tierlist", label: "Tier lists" },
] as const;

/**
 * Nav de desktop — el corte de 14° marca el link activo.
 * En móvil la navegación vive en MobileBottomNav (ya no hay hamburguesa).
 */
export default function NavMenu() {
  const pathname = usePathname();

  const isActive = (href: string) =>
    href === "/" ? pathname === "/" : pathname.startsWith(href);

  return (
    <nav className="hidden md:flex items-center gap-0.5">
      {NAV_LINKS.map(({ href, label }) => {
        const active = isActive(href);
        return (
          <Link
            key={href}
            href={href}
            className={`flex items-center gap-[7px] px-3 py-2 text-sm whitespace-nowrap transition-colors duration-fast ${
              active ? "font-bold text-ink-1" : "font-medium text-ink-3 hover:text-ink-1"
            }`}
          >
            {active && <span className="sh-cut !mr-0 !w-[3px] !h-3" />}
            {label}
          </Link>
        );
      })}
    </nav>
  );
}
