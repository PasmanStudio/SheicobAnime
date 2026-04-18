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
    // No h-screen here — the layout must be content-height, not viewport-height.
    // If we used h-screen, the inner div would grow whenever the parent
    // (EmbeddedPlayerFrame) enlarged the iframe, triggering ResizeObserver →
    // postMessage → parent grows again → infinite loop.
    <div className="w-screen bg-black">
      {children}
    </div>
  );
}
