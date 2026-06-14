import AdsterraGlobalAds from "@/components/ads/AdsterraGlobalAds";
import ConsentBanner from "@/components/ads/ConsentBanner";
import PageViewTracker from "@/components/ads/PageViewTracker";
import Analytics from "@/components/Analytics";
import AuthProvider from "@/components/auth/AuthProvider";
import MobileBottomNav from "@/components/layout/MobileBottomNav";
import SiteFooter from "@/components/layout/SiteFooter";
import SiteHeader from "@/components/layout/SiteHeader";
import ServiceWorkerRegister from "@/components/ServiceWorkerRegister";
import { siteUrl } from "@/lib/site-url";
import type { Metadata, Viewport } from "next";
import { Archivo } from "next/font/google";
import localFont from "next/font/local";
import "../globals.css";

// Display: Archivo expandido (eje wdth habilita el 118% del design system)
const archivo = Archivo({
  subsets: ["latin"],
  style: ["normal", "italic"],
  axes: ["wdth"],
  variable: "--font-display",
});
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

export const viewport: Viewport = {
  width: "device-width",
  initialScale: 1,
  viewportFit: "cover",
  themeColor: "#07090E", // barra de estado / chrome del navegador en el abismo
};

export const metadata: Metadata = {
  metadataBase: new URL(siteUrl()),
  title: {
    default: "SheicobAnime — Watch Anime Online",
    template: "%s | SheicobAnime",
  },
  description: "Discover and watch anime episodes online. Updated daily.",
  manifest: "/manifest.webmanifest",
  appleWebApp: {
    capable: true,
    statusBarStyle: "black-translucent",
    title: "SheicobAnime",
  },
  icons: {
    icon: "/favicon.png",
    shortcut: "/favicon.png",
    apple: "/apple-touch-icon.png",
  },
  openGraph: {
    siteName: "SheicobAnime",
    locale: "es_AR",
  },
  twitter: {
    card: "summary_large_image",
    site: "@sheicobanime",
  },
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="es" className="dark">
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
        className={`${archivo.variable} ${geistSans.variable} ${geistMono.variable} antialiased bg-abyss-0 text-ink-1 min-h-screen flex flex-col`}
      >
        <AuthProvider>
          <SiteHeader />
          {/* pb extra en móvil para que la bottom nav no tape contenido */}
          <main className="flex-1 pb-14 md:pb-0">{children}</main>
          <SiteFooter />
          <MobileBottomNav />
          <ConsentBanner />
          <AdsterraGlobalAds />
          <PageViewTracker />
          <Analytics />
          <ServiceWorkerRegister />
        </AuthProvider>
      </body>
    </html>
  );
}
