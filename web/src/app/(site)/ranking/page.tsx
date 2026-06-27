import AdSlot from "@/components/ads/AdSlot";
import RankNumber from "@/components/ui/RankNumber";
import SectionHeader from "@/components/ui/SectionHeader";
import type { RankingEntry } from "@/app/api/ranking/route";
import type { Metadata } from "next";
import Image from "next/image";
import Link from "next/link";

// Respaldo de fondo; la frescura la da la purga on-demand. Alto para no quemar
// el free tier de writes de KV (antes 60s = hasta 1440 writes/día solo esta ruta).
export const revalidate = 3600;

export const metadata: Metadata = {
  title: "Ranking de Anime",
  description: "Los animes más populares de SheicobAnime según los likes de los usuarios.",
  alternates: { canonical: "/ranking" },
  openGraph: {
    title: "Ranking de Anime — SheicobAnime",
    description: "Los animes más populares según los likes de nuestra comunidad.",
    type: "website",
    url: "/ranking",
  },
};

async function getRanking(): Promise<RankingEntry[]> {
  try {
    // Import getDb directly — server component, same process
    const { getDb } = await import("@/lib/db");
    const db = getDb();
    const { rows } = await db.query<{
      series_slug: string;
      series_title: string;
      cover_url: string | null;
      like_count: string;
    }>(
      `SELECT
        series_slug,
        series_title,
        cover_url,
        COUNT(*)::text AS like_count
       FROM user_anime_likes
       GROUP BY series_slug, series_title, cover_url
       ORDER BY like_count DESC, series_title ASC
       LIMIT 50`,
    );
    return rows.map((r) => ({
      series_slug: r.series_slug,
      series_title: r.series_title,
      cover_url: r.cover_url,
      like_count: parseInt(r.like_count, 10),
    }));
  } catch {
    return [];
  }
}

export default async function RankingPage() {
  const entries = await getRanking();

  return (
    <div className="mx-auto max-w-3xl px-4 py-8">
      {/* Header */}
      <SectionHeader
        size="lg"
        eyebrow="Votado por la comunidad"
        title="Ranking de anime"
        className="mb-8"
      />

      {entries.length === 0 ? (
        <div className="py-24 text-center text-sm">
          <p className="text-ink-2">Todavía no hay likes registrados.</p>
          <p className="mt-1 text-ink-3">
            Visita cualquier anime y presiona el botón ♥ para aparecer aquí.
          </p>
        </div>
      ) : (
        <ol className="space-y-2">
          {entries.map((entry, idx) => {
            const podium = idx < 3;
            return (
              <li key={entry.series_slug}>
                <Link
                  href={`/series/${entry.series_slug}`}
                  className={`group flex items-center gap-4 rounded-card px-3 py-2.5 transition-all duration-fast border ${
                    podium
                      ? "bg-abyss-2 border-line-1 hover:border-line-2"
                      : "border-transparent hover:bg-abyss-2 hover:border-line-1"
                  }`}
                >
                  {/* Position — #1 dorado, #2 plata, #3 bronce */}
                  <RankNumber rank={idx + 1} size={podium ? "lg" : "md"} />

                  {/* Cover */}
                  <div className="shrink-0 w-[42px] aspect-[2/3] rounded-badge overflow-hidden bg-abyss-3">
                    {entry.cover_url ? (
                      <Image
                        src={entry.cover_url}
                        alt={entry.series_title}
                        width={42}
                        height={63}
                        className="w-full h-full object-cover"
                      />
                    ) : (
                      <div className="flex h-full w-full items-center justify-center font-display italic font-black text-ink-3">
                        {entry.series_title.trim()[0]?.toUpperCase() ?? "?"}
                      </div>
                    )}
                  </div>

                  {/* Title */}
                  <span className="sh-title flex-1 min-w-0 truncate text-sm group-hover:text-[var(--cyan-200)] transition-colors duration-fast">
                    {entry.series_title}
                  </span>

                  {/* Like count — mono, dorado para el #1 */}
                  <span
                    className="sh-stat shrink-0 text-[13px] min-w-[70px] text-right"
                    style={{ color: idx === 0 ? "var(--gold)" : "var(--text-2)" }}
                  >
                    ♥ {entry.like_count.toLocaleString("es-AR")}
                  </span>
                </Link>
              </li>
            );
          })}
        </ol>
      )}

      <div className="mt-8">
        <AdSlot placement="profile_bottom" />
      </div>
    </div>
  );
}
