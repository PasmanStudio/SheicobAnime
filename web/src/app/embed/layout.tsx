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
  variable: "--font-body",
  weight: "100 900",
});
const geistMono = localFont({
  src: "../fonts/GeistMonoVF.woff",
  variable: "--font-mono",
  weight: "100 900",
});

export const metadata: Metadata = {
  robots: { index: false, follow: false },
};

export default function EmbedRootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en" className="dark">
      <head>
        {/*
          ExoClick Client Hints delegation — the VAST preroll fetches from
          s.magsrv.com inside this iframe, so the embed layout needs this tag
          too (not just the site layout).
        */}
        <meta
          httpEquiv="Delegate-CH"
          content="Sec-CH-UA https://s.magsrv.com; Sec-CH-UA-Mobile https://s.magsrv.com; Sec-CH-UA-Arch https://s.magsrv.com; Sec-CH-UA-Model https://s.magsrv.com; Sec-CH-UA-Platform https://s.magsrv.com; Sec-CH-UA-Platform-Version https://s.magsrv.com; Sec-CH-UA-Bitness https://s.magsrv.com; Sec-CH-UA-Full-Version-List https://s.magsrv.com; Sec-CH-UA-Full-Version https://s.magsrv.com;"
        />
      </head>
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
