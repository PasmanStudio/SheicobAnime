import Link from "next/link";
import Image from "next/image";
import SearchBar from "./SearchBar";
import NavMenu from "./NavMenu";

export default function SiteHeader() {
  return (
    <header className="sticky top-0 z-40 bg-neutral-950/95 backdrop-blur-md border-b border-neutral-800/60">
      <div className="container mx-auto flex items-center gap-3 h-16 px-4 relative">
        {/* Logo */}
        <Link href="/" className="shrink-0 flex items-center">
          <Image
            src="/logo.png"
            alt="SheicobAnime"
            width={180}
            height={40}
            priority
            className="h-8 sm:h-10 w-auto max-w-[110px] sm:max-w-[160px] lg:max-w-none"
            style={{ mixBlendMode: "screen" }}
          />
        </Link>

        {/* Nav (desktop + mobile hamburger) */}
        <NavMenu />

        {/* Search — pushed to the right */}
        <div className="flex-1 flex justify-end">
          <SearchBar />
        </div>
      </div>

      {/* Accent line */}
      <div className="h-px bg-gradient-to-r from-transparent via-cyan-500/40 to-transparent" />
    </header>
  );
}
