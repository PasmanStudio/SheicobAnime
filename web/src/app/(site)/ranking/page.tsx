import AdSlot from "@/components/ads/AdSlot";
import type { RankingEntry } from "@/app/api/ranking/route";
import type { Metadata } from "next";
import Image from "next/image";
import Link from "next/link";

export const revalidate = 60;

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
    <div className="container mx-auto px-4 py-8 max-w-3xl">
      {/* Header */}
      <div className="mb-8">
        <h1 className="text-2xl md:text-3xl font-bold text-white flex items-center gap-3">
          <span className="text-rose-400" aria-hidden>♥</span>
          Ranking de Anime
        </h1>
        <p className="mt-1 text-sm text-neutral-500">
          Los más populares de nuestra comunidad, ordenados por likes.
        </p>
      </div>

      <AdSlot placement="profile_top" />

      {entries.length === 0 ? (
        <div className="text-center py-24">
          <p className="text-5xl mb-4">🏆</p>
          <p className="text-neutral-400 mb-1">Todavía no hay likes registrados.</p>
          <p className="text-sm text-neutral-600">
            Visitá cualquier anime y presioná el botón ♥ para aparecer acá.
          </p>
        </div>
      ) : (
        <ol className="space-y-2">
          {entries.map((entry, idx) => (
            <li key={entry.series_slug}>
              <Link
                href={`/series/${entry.series_slug}`}
                className="flex items-center gap-4 px-4 py-3 rounded-xl bg-neutral-900 border border-neutral-800
                  hover:border-neutral-700 hover:bg-neutral-800/70 transition-colors group"
              >
                {/* Position */}
                <span
                  className={`w-8 shrink-0 text-center text-sm font-bold tabular-nums
                    ${idx === 0 ? "text-yellow-400" : idx === 1 ? "text-neutral-300" : idx === 2 ? "text-amber-600" : "text-neutral-600"}`}
                >
                  {idx + 1}
                </span>

                {/* Cover */}
                <div className="shrink-0 w-10 h-14 rounded overflow-hidden bg-neutral-800">
                  {entry.cover_url ? (
                    <Image
                      src={entry.cover_url}
                      alt={entry.series_title}
                      width={40}
                      height={56}
                      className="w-full h-full object-cover"
                    />
                  ) : (
                    <div className="w-full h-full flex items-center justify-center text-neutral-600 text-lg">🎬</div>
                  )}
                </div>

                {/* Title */}
                <span className="flex-1 min-w-0 text-sm font-medium text-neutral-200 group-hover:text-white transition-colors truncate">
                  {entry.series_title}
                </span>

                {/* Like count */}
                <span className="shrink-0 flex items-center gap-1.5 text-sm text-rose-400 font-semibold tabular-nums">
                  <svg className="w-3.5 h-3.5" viewBox="0 0 24 24" fill="currentColor" aria-hidden>
                    <path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z" />
                  </svg>
                  {entry.like_count.toLocaleString("es-AR")}
                </span>
              </Link>
            </li>
          ))}
        </ol>
      )}

      <div className="mt-8">
        <AdSlot placement="profile_bottom" />
      </div>
    </div>
  );
}
