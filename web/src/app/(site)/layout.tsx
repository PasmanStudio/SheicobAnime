import type { Metadata } from "next";
import localFont from "next/font/local";
import "../globals.css";
import SiteHeader from "@/components/layout/SiteHeader";
import ConsentBanner from "@/components/ads/ConsentBanner";
import AdsterraGlobalAds from "@/components/ads/AdsterraGlobalAds";

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
  title: {
    default: "SheicobAnime — Watch Anime Online",
    template: "%s | SheicobAnime",
  },
  description: "Discover and watch anime episodes online. Updated daily.",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en" className="dark">
      <head>
        {/*
          ExoClick Client Hints delegation — improves VAST ad targeting for the
          in-video preroll zone served from s.magsrv.com. Required only once per page.
          https://help.exoclient.com/en/articles/client-hints
        */}
        <meta
          httpEquiv="Delegate-CH"
          content="Sec-CH-UA https://s.magsrv.com; Sec-CH-UA-Mobile https://s.magsrv.com; Sec-CH-UA-Arch https://s.magsrv.com; Sec-CH-UA-Model https://s.magsrv.com; Sec-CH-UA-Platform https://s.magsrv.com; Sec-CH-UA-Platform-Version https://s.magsrv.com; Sec-CH-UA-Bitness https://s.magsrv.com; Sec-CH-UA-Full-Version-List https://s.magsrv.com; Sec-CH-UA-Full-Version https://s.magsrv.com;"
        />
      </head>
      <body
        className={`${geistSans.variable} ${geistMono.variable} antialiased bg-neutral-950 text-white min-h-screen flex flex-col`}
      >
        <SiteHeader />
        <main className="flex-1">{children}</main>
        <footer className="border-t border-neutral-800 py-6 text-center text-xs text-neutral-600">
          <p>© {new Date().getFullYear()} SheicobAnime — indexes publicly embeddable mirrors only.</p>
          <p className="mt-2">
            <a href="/privacy" className="hover:text-neutral-400 underline">Política de Privacidad</a>
            {" · "}
            <a href="/dmca" className="hover:text-neutral-400 underline">DMCA</a>
          </p>
        </footer>
        <ConsentBanner />
        <AdsterraGlobalAds />
      </body>
    </html>
  );
}
