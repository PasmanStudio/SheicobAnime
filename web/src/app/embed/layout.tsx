import type { Metadata } from "next";

/**
 * Minimal layout for the /embed iframe route — no header, footer, ads, or
 * consent banner. Served with X-Frame-Options: SAMEORIGIN (see vercel.json)
 * so the parent site can embed it while third parties cannot.
 */
export const metadata: Metadata = {
  robots: { index: false, follow: false },
};

export default function EmbedLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <div className="w-screen h-screen bg-black overflow-hidden">
      {children}
    </div>
  );
}
