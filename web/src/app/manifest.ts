import type { MetadataRoute } from "next";

// Web App Manifest — hace el sitio instalable ("Agregar a inicio").
// Next.js lo sirve en /manifest.webmanifest automáticamente.
export default function manifest(): MetadataRoute.Manifest {
  return {
    name: "SheicobAnime",
    short_name: "SheicobAnime",
    description: "Mira anime online en español. Catálogo actualizado todos los días.",
    start_url: "/",
    scope: "/",
    display: "standalone",
    orientation: "portrait",
    background_color: "#07090E", // --bg-0 (el abismo)
    theme_color: "#07090E",
    lang: "es-AR",
    categories: ["entertainment"],
    icons: [
      { src: "/icon-192.png", sizes: "192x192", type: "image/png", purpose: "any" },
      { src: "/icon-512.png", sizes: "512x512", type: "image/png", purpose: "any" },
      { src: "/icon-maskable-512.png", sizes: "512x512", type: "image/png", purpose: "maskable" },
    ],
  };
}
