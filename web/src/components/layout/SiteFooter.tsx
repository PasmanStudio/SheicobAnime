import { siteUrl } from "@/lib/site-url";
import Image from "next/image";
import Link from "next/link";

const DISCORD_INVITE = process.env.NEXT_PUBLIC_DISCORD_INVITE ?? "";
const TELEGRAM_CHANNEL = process.env.NEXT_PUBLIC_TELEGRAM_CHANNEL ?? "";

export default function SiteFooter() {
  return (
    <footer className="mt-14 border-t border-line-1 bg-abyss-1 py-7 pb-[calc(2.25rem+env(safe-area-inset-bottom))]">
      <div className="mx-auto max-w-container px-5 flex flex-wrap items-center justify-between gap-4">
        <Image
          src="/logo.png"
          alt="SheicobAnime"
          width={150}
          height={32}
          className="h-[30px] w-auto"
          style={{ mixBlendMode: "screen" }}
        />
        <nav className="flex flex-wrap gap-5 text-[13px] text-ink-3">
          <Link href="/dmca" className="hover:text-ink-1 transition-colors duration-fast">
            DMCA
          </Link>
          <Link href="/privacy" className="hover:text-ink-1 transition-colors duration-fast">
            Privacidad
          </Link>
          {DISCORD_INVITE && (
            <Link
              href={DISCORD_INVITE}
              target="_blank"
              rel="noopener noreferrer"
              className="hover:text-ink-1 transition-colors duration-fast"
            >
              Discord
            </Link>
          )}
          {TELEGRAM_CHANNEL && (
            <Link
              href={TELEGRAM_CHANNEL}
              target="_blank"
              rel="noopener noreferrer"
              className="hover:text-ink-1 transition-colors duration-fast"
            >
              Telegram
            </Link>
          )}
          <Link href="/apoyanos" className="hover:text-brand-bright transition-colors duration-fast">
            Apoyanos
          </Link>
        </nav>
        <span className="font-mono text-[11px] text-ink-3">
          {siteUrl().replace(/^https?:\/\//, "")} — hecho por fans, para fans
        </span>
      </div>
    </footer>
  );
}
