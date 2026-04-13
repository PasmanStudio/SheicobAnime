import type { Metadata } from "next";
import localFont from "next/font/local";
import "./globals.css";
import SiteHeader from "@/components/layout/SiteHeader";
import ConsentBanner from "@/components/ads/ConsentBanner";
import AdsterraGlobalAds from "@/components/ads/AdsterraGlobalAds";

const geistSans = localFont({
  src: "./fonts/GeistVF.woff",
  variable: "--font-geist-sans",
  weight: "100 900",
});
const geistMono = localFont({
  src: "./fonts/GeistMonoVF.woff",
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
