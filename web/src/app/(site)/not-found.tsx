import type { Metadata } from "next";
import Link from "next/link";

export const metadata: Metadata = {
  title: "404 — Page Not Found",
  description: "The page you are looking for does not exist.",
};

export default function NotFound() {
  return (
    <div className="container mx-auto px-4 py-24 text-center space-y-6">
      <p className="text-8xl font-black text-brand leading-none">404</p>
      <h1 className="text-2xl font-bold text-white">Page Not Found</h1>
      <p className="text-ink-2 max-w-sm mx-auto">
        The page you&apos;re looking for doesn&apos;t exist or has been moved.
      </p>
      <div className="flex justify-center gap-3 pt-2">
        <Link
          href="/"
          className="px-5 py-2.5 rounded-lg bg-brand text-[var(--text-on-accent)] hover:brightness-110 text-sm font-medium transition-colors"
        >
          Go Home
        </Link>
        <Link
          href="/search"
          className="px-5 py-2.5 rounded-lg border border-line-2 hover:border-line-2 text-ink-2 hover:text-white text-sm font-medium transition-colors"
        >
          Browse Anime
        </Link>
      </div>
    </div>
  );
}
