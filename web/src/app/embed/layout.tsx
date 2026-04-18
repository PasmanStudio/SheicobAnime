import type { Metadata } from "next";
import localFont from "next/font/local";
import "../globals.css";

/**
 * TRUE root layout for the /embed iframe route. This is a sibling root layout
 * to /(site)/layout.tsx via Next.js Route Groups — Next renders this <html>
 * (no SiteHeader, footer, consent banner, or global ads) for any /embed/* path,
 * isolating the iframe content from the rest of the site chrome.
 *
 * Served with X-Frame-Options: SAMEORIGIN (see vercel.json) so the parent site
 * can embed it while third parties cannot.
 */
const geistSans = localFont({
  src: "../fonts/GeistVF.woff",
  variable: "--font-geist-sans",
  weight: "100 900",
});
const geistMono = localFont({
  src: "../fonts/GeistMonoVF.woff",
  variable: "--font-geist-mono",
  weight: "100 900",
});

export const metadata: Metadata = {
  robots: { index: false, follow: false },
};

export default function EmbedRootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en" className="dark">
      <body
        className={`${geistSans.variable} ${geistMono.variable} antialiased bg-black text-white`}
      >
        {/* No h-screen / min-h-screen — content-height only, so EmbedHeightReporter
            doesn't get into a feedback loop with the parent iframe resize. */}
        <div className="w-screen bg-black">{children}</div>
      </body>
    </html>
  );
}
