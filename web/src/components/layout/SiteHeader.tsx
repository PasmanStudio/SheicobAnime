import UserButton from "@/components/auth/UserButton";
import Link from "next/link";
import Image from "next/image";
import SearchBar from "./SearchBar";
import NavMenu from "./NavMenu";

const DISCORD_INVITE = process.env.NEXT_PUBLIC_DISCORD_INVITE ?? "";
const TELEGRAM_CHANNEL = process.env.NEXT_PUBLIC_TELEGRAM_CHANNEL ?? "";

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

        {/* Search + Telegram + Discord + user — pushed to the right */}
        <div className="flex-1 flex items-center justify-end gap-2">
          <SearchBar />
          {TELEGRAM_CHANNEL && (
            <Link
              href={TELEGRAM_CHANNEL}
              target="_blank"
              rel="noopener noreferrer"
              aria-label="Unirse al canal de Telegram"
              className="hidden sm:flex items-center gap-1.5 px-3 py-1.5 rounded-md text-sm font-medium text-neutral-400 hover:text-[#2AABEE] hover:bg-neutral-800/60 transition-colors"
            >
              {/* Telegram logo */}
              <svg className="w-4 h-4 shrink-0" viewBox="0 0 24 24" fill="currentColor">
                <path d="M12 0C5.373 0 0 5.373 0 12s5.373 12 12 12 12-5.373 12-12S18.627 0 12 0zm5.562 8.248-1.97 9.289c-.145.658-.537.818-1.084.508l-3-2.21-1.447 1.394c-.16.16-.295.295-.605.295l.213-3.053 5.56-5.023c.242-.213-.054-.333-.373-.12L7.19 14.447l-2.95-.924c-.64-.203-.654-.64.136-.948l11.526-4.445c.537-.194 1.006.131.66.118z" />
              </svg>
              <span className="hidden lg:inline">Telegram</span>
            </Link>
          )}
          {DISCORD_INVITE && (
            <Link
              href={DISCORD_INVITE}
              target="_blank"
              rel="noopener noreferrer"
              aria-label="Unirse al Discord"
              className="hidden sm:flex items-center gap-1.5 px-3 py-1.5 rounded-md text-sm font-medium text-neutral-400 hover:text-white hover:bg-neutral-800/60 transition-colors"
            >
              {/* Discord logo */}
              <svg className="w-4 h-4 shrink-0" viewBox="0 0 24 24" fill="currentColor">
                <path d="M20.317 4.37a19.791 19.791 0 0 0-4.885-1.515.074.074 0 0 0-.079.037c-.21.375-.444.864-.608 1.25a18.27 18.27 0 0 0-5.487 0 12.64 12.64 0 0 0-.617-1.25.077.077 0 0 0-.079-.037A19.736 19.736 0 0 0 3.677 4.37a.07.07 0 0 0-.032.027C.533 9.046-.32 13.58.099 18.057c.002.022.015.04.033.05a19.9 19.9 0 0 0 5.993 3.03.078.078 0 0 0 .084-.028c.462-.63.874-1.295 1.226-1.994a.076.076 0 0 0-.041-.106 13.107 13.107 0 0 1-1.872-.892.077.077 0 0 1-.008-.128 10.2 10.2 0 0 0 .372-.292.074.074 0 0 1 .077-.01c3.928 1.793 8.18 1.793 12.062 0a.074.074 0 0 1 .078.01c.12.098.246.198.373.292a.077.077 0 0 1-.006.127 12.299 12.299 0 0 1-1.873.892.077.077 0 0 0-.041.107c.36.698.772 1.362 1.225 1.993a.076.076 0 0 0 .084.028 19.839 19.839 0 0 0 6.002-3.03.077.077 0 0 0 .032-.054c.5-5.177-.838-9.674-3.549-13.66a.061.061 0 0 0-.031-.03zM8.02 15.33c-1.183 0-2.157-1.085-2.157-2.419 0-1.333.956-2.419 2.157-2.419 1.21 0 2.176 1.096 2.157 2.42 0 1.333-.956 2.418-2.157 2.418zm7.975 0c-1.183 0-2.157-1.085-2.157-2.419 0-1.333.955-2.419 2.157-2.419 1.21 0 2.176 1.096 2.157 2.42 0 1.333-.946 2.418-2.157 2.418z" />
              </svg>
              <span className="hidden lg:inline">Discord</span>
            </Link>
          )}
          <UserButton />
        </div>
      </div>

      {/* Accent line */}
      <div className="h-px bg-gradient-to-r from-transparent via-cyan-500/40 to-transparent" />
    </header>
  );
}
