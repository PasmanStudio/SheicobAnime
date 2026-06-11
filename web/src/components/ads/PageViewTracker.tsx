"use client";

import { recordPageView } from "@/lib/ad-frequency";
import { usePathname } from "next/navigation";
import { useEffect } from "react";

/**
 * Cuenta pageviews de la sesión — el interstitial no aparece antes del 3er
 * pageview (cap del doc de ads). Montar una sola vez en el layout del sitio.
 */
export default function PageViewTracker() {
  const pathname = usePathname();

  useEffect(() => {
    recordPageView();
  }, [pathname]);

  return null;
}
