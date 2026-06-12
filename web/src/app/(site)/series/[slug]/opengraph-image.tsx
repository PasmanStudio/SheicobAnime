import { getSeriesBySlug } from "@/lib/api";
import { siteUrl } from "@/lib/site-url";
import { ImageResponse } from "next/og";

// OG image por serie: poster + título + score + logo sobre fondo abismo.
// Mejora el CTR al compartir en Discord/Telegram/WhatsApp (doc 1, SEO quick wins).

export const alt = "SheicobAnime";
export const size = { width: 1200, height: 630 };
export const contentType = "image/png";

const STATUS_LABELS: Record<string, string> = {
  ongoing: "EN EMISIÓN",
  completed: "CONCLUIDO",
  upcoming: "POR ESTRENAR",
  hiatus: "EN PAUSA",
};

export default async function Image({ params }: { params: Promise<{ slug: string }> }) {
  const { slug } = await params;

  let series;
  try {
    series = await getSeriesBySlug(slug);
  } catch {
    series = null;
  }

  const scoreColor =
    series?.score == null
      ? "#8B93A1"
      : series.score >= 8
        ? "#4ADE8C"
        : series.score >= 6
          ? "#FFC53D"
          : "#8B93A1";

  return new ImageResponse(
    (
      <div
        style={{
          width: "100%",
          height: "100%",
          display: "flex",
          background: "#07090E",
          // Speedlines sutiles — la textura de la marca
          backgroundImage:
            "repeating-linear-gradient(104deg, transparent 0px, transparent 46px, rgba(56,203,250,0.05) 46px, rgba(56,203,250,0.05) 48px)",
          padding: 56,
          alignItems: "center",
          gap: 56,
          fontFamily: "sans-serif",
        }}
      >
        {/* Poster 2:3 */}
        {series?.coverUrl ? (
          // eslint-disable-next-line @next/next/no-img-element
          <img
            src={series.coverUrl}
            alt=""
            width={346}
            height={518}
            style={{
              width: 346,
              height: 518,
              objectFit: "cover",
              borderRadius: 20,
              border: "2px solid #2A3650",
            }}
          />
        ) : (
          <div
            style={{
              width: 346,
              height: 518,
              borderRadius: 20,
              border: "2px solid #2A3650",
              background: "linear-gradient(152deg, #143A52 0%, #0B1626 100%)",
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
              fontSize: 160,
              fontStyle: "italic",
              fontWeight: 800,
              color: "rgba(255,255,255,0.16)",
            }}
          >
            {series?.title?.trim()[0]?.toUpperCase() ?? "S"}
          </div>
        )}

        {/* Info */}
        <div
          style={{
            display: "flex",
            flexDirection: "column",
            flex: 1,
            gap: 24,
            justifyContent: "center",
          }}
        >
          {/* Eyebrow: corte + estado */}
          <div style={{ display: "flex", alignItems: "center", gap: 14 }}>
            <div
              style={{
                width: 10,
                height: 30,
                background: "linear-gradient(135deg, #38CBFA 0%, #14B1E7 55%, #0E84CE 100%)",
                transform: "skewX(-14deg)",
                borderRadius: 2,
              }}
            />
            <div
              style={{
                fontSize: 24,
                fontWeight: 700,
                letterSpacing: "0.14em",
                color: "#6EDDFF",
              }}
            >
              {(series?.status && STATUS_LABELS[series.status]) || "ANIME ONLINE"}
              {series?.year ? ` · ${series.year}` : ""}
            </div>
          </div>

          {/* Título */}
          <div
            style={{
              fontSize: series && series.title.length > 40 ? 52 : 64,
              fontWeight: 800,
              fontStyle: "italic",
              textTransform: "uppercase",
              lineHeight: 1.1,
              color: "#F2F6FB",
              display: "flex",
            }}
          >
            {series?.title ?? "SheicobAnime"}
          </div>

          {/* Score + episodios en mono */}
          <div style={{ display: "flex", alignItems: "center", gap: 20 }}>
            {series?.score != null && (
              <div
                style={{
                  display: "flex",
                  alignItems: "center",
                  gap: 10,
                  padding: "10px 22px",
                  borderRadius: 12,
                  background: "#19202F",
                  border: "2px solid #2A3650",
                  fontSize: 36,
                  fontWeight: 700,
                  color: scoreColor,
                }}
              >
                ★ {series.score.toFixed(1)}
              </div>
            )}
            {series?.episodeCount ? (
              <div style={{ fontSize: 28, color: "#97A3B8", display: "flex" }}>
                {series.episodeCount} episodios
              </div>
            ) : null}
          </div>

          {/* Logo + dominio */}
          <div style={{ display: "flex", alignItems: "center", gap: 18, marginTop: 18 }}>
            {/* eslint-disable-next-line @next/next/no-img-element */}
            <img src={`${siteUrl()}/logo.png`} alt="SheicobAnime" height={56} style={{ height: 56 }} />
            <div
              style={{
                fontSize: 22,
                color: "#5B6678",
                letterSpacing: "0.1em",
                display: "flex",
              }}
            >
              {siteUrl().replace(/^https?:\/\//, "")}
            </div>
          </div>
        </div>
      </div>
    ),
    size,
  );
}
