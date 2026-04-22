import Link from "next/link";
import Image from "next/image";
import SearchBar from "./SearchBar";

const NAV_LINKS = [
  { href: "/", label: "Inicio" },
  { href: "/directory", label: "Directorio" },
  { href: "/genres", label: "Géneros" },
] as const;

export default function SiteHeader() {
  return (
    <header className="sticky top-0 z-40 bg-neutral-900/95 backdrop-blur border-b border-neutral-800">
      <div className="container mx-auto flex items-center gap-4 h-14 px-4">
        {/* Logo */}
        <Link href="/" className="shrink-0">
          <Image
            src="/logo.png"
            alt="SheicobAnime"
            width={160}
            height={36}
            priority
            className="h-9 w-auto"
          />
        </Link>

        {/* Desktop nav */}
        <nav className="hidden md:flex items-center gap-5 text-sm text-neutral-400">
          {NAV_LINKS.map(({ href, label }) => (
            <Link
              key={href}
              href={href}
              className="hover:text-white transition-colors"
            >
              {label}
            </Link>
          ))}
        </nav>

        {/* Search — pushed to the right */}
        <div className="flex-1 flex justify-end">
          <SearchBar />
        </div>
      </div>
    </header>
  );
}
