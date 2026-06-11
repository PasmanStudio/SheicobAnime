"use client";

import LoginModal from "@/components/auth/LoginModal";
import { useSession } from "next-auth/react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useState } from "react";

const ICONS = {
  home: (
    <>
      <path d="m3 9 9-7 9 7v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />
      <polyline points="9 22 9 12 15 12 15 22" />
    </>
  ),
  compass: (
    <>
      <circle cx="12" cy="12" r="10" />
      <polygon points="16.24 7.76 14.12 14.12 7.76 16.24 9.88 9.88 16.24 7.76" />
    </>
  ),
  trophy: (
    <>
      <path d="M6 9H4.5a2.5 2.5 0 0 1 0-5H6" />
      <path d="M18 9h1.5a2.5 2.5 0 0 0 0-5H18" />
      <path d="M4 22h16" />
      <path d="M10 14.66V17c0 .55-.47.98-.97 1.21C7.85 18.75 7 20.24 7 22" />
      <path d="M14 14.66V17c0 .55.47.98.97 1.21C16.15 18.75 17 20.24 17 22" />
      <path d="M18 2H6v7a6 6 0 0 0 12 0V2Z" />
    </>
  ),
  user: (
    <>
      <circle cx="12" cy="8" r="5" />
      <path d="M20 21a8 8 0 0 0-16 0" />
    </>
  ),
} as const;

function NavIcon({ name }: { name: keyof typeof ICONS }) {
  return (
    <svg
      width={21}
      height={21}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden
    >
      {ICONS[name]}
    </svg>
  );
}

/**
 * Bottom nav fija de 4 destinos — reemplaza al menú hamburguesa en móvil.
 * El corte de 14° asoma desde arriba como indicador del tab activo.
 */
export default function MobileBottomNav() {
  const pathname = usePathname();
  const { data: session } = useSession();
  const [showLogin, setShowLogin] = useState(false);

  const user = session?.user as
    | { id?: string; username?: string | null }
    | undefined;
  const profileHref = user ? `/usuario/${user.username ?? user.id}` : null;

  const items: Array<{
    id: string;
    label: string;
    icon: keyof typeof ICONS;
    href: string | null;
    isActive: boolean;
  }> = [
    { id: "inicio", label: "Inicio", icon: "home", href: "/", isActive: pathname === "/" },
    {
      id: "directorio",
      label: "Directorio",
      icon: "compass",
      href: "/directory",
      isActive: pathname.startsWith("/directory") || pathname.startsWith("/genres"),
    },
    {
      id: "ranking",
      label: "Ranking",
      icon: "trophy",
      href: "/ranking",
      isActive: pathname.startsWith("/ranking") || pathname.startsWith("/tierlist"),
    },
    {
      id: "perfil",
      label: "Perfil",
      icon: "user",
      href: profileHref,
      isActive: pathname.startsWith("/usuario"),
    },
  ];

  return (
    <>
      <nav
        className="md:hidden fixed bottom-0 inset-x-0 z-40 grid grid-cols-4 border-t border-line-1 bg-[rgba(12,16,26,0.94)] backdrop-blur-[12px] pb-[env(safe-area-inset-bottom)]"
        aria-label="Navegación principal"
      >
        {items.map((item) => {
          const inner = (
            <>
              <span
                className="absolute top-0 h-[3px] w-[26px] rounded-b-[3px]"
                style={{
                  background: item.isActive ? "var(--grad-action)" : "transparent",
                  transform: "skewX(-14deg)",
                }}
              />
              <NavIcon name={item.icon} />
              <span className={`text-[10px] ${item.isActive ? "font-bold" : "font-medium"}`}>
                {item.label}
              </span>
            </>
          );
          const cls = `relative flex min-h-[56px] flex-col items-center justify-center gap-[3px] pt-[7px] pb-[9px] transition-colors duration-fast ${
            item.isActive ? "text-brand-bright" : "text-ink-3"
          }`;
          return item.href ? (
            <Link key={item.id} href={item.href} className={cls}>
              {inner}
            </Link>
          ) : (
            <button key={item.id} className={cls} onClick={() => setShowLogin(true)}>
              {inner}
            </button>
          );
        })}
      </nav>
      {showLogin && <LoginModal onClose={() => setShowLogin(false)} />}
    </>
  );
}
