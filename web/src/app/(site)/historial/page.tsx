import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import type { EpisodeHistoryEntry } from "@/lib/watchlist";
import AdSlot from "@/components/ads/AdSlot";
import type { Metadata } from "next";
import Image from "next/image";
import Link from "next/link";

export const metadata: Metadata = {
  title: "Historial — SheicobAnime",
};

function timeAgo(date: string): string {
  const diff = Date.now() - new Date(date).getTime();
  const mins = Math.floor(diff / 60_000);
  if (mins < 60) return `hace ${mins} min`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `hace ${hrs}h`;
  const days = Math.floor(hrs / 24);
  if (days < 7) return `hace ${days}d`;
  return new Date(date).toLocaleDateString("es-AR", { day: "numeric", month: "short" });
}

async function getHistory(userId: string): Promise<EpisodeHistoryEntry[]> {
  try {
    const db = getDb();
    const { rows } = await db.query<EpisodeHistoryEntry>(
      `SELECT * FROM user_episode_history WHERE user_id = $1 ORDER BY watched_at DESC LIMIT 100`,
      [userId],
    );
    return rows;
  } catch {
    return [];
  }
}

// Group episodes by date
function groupByDate(entries: EpisodeHistoryEntry[]): Map<string, EpisodeHistoryEntry[]> {
  const map = new Map<string, EpisodeHistoryEntry[]>();
  for (const e of entries) {
    const date = new Date(e.watched_at);
    const key = date.toLocaleDateString("es-AR", { weekday: "long", day: "numeric", month: "long" });
    if (!map.has(key)) map.set(key, []);
    map.get(key)!.push(e);
  }
  return map;
}

export default async function HistorialPage() {
  const session = await auth();

  if (!session?.user?.id) {
    return (
      <div className="container mx-auto px-4 py-16 max-w-2xl text-center">
        <p className="text-5xl mb-4">🔒</p>
        <h1 className="text-2xl font-bold text-white mb-2">Iniciá sesión</h1>
        <p className="text-neutral-400 mb-6">
          Necesitás una cuenta para ver tu historial de episodios.
        </p>
        <Link
          href="/"
          className="inline-flex items-center gap-2 px-5 py-2.5 rounded-lg bg-indigo-600 hover:bg-indigo-500 text-white font-medium transition-colors"
        >
          Volver al inicio
        </Link>
      </div>
    );
  }

  const history = await getHistory(session.user.id);
  const grouped = groupByDate(history);

  return (
    <div className="container mx-auto px-4 py-8 max-w-3xl">
      <div className="flex items-center justify-between mb-6">
        <h1 className="text-2xl font-bold text-white">Historial</h1>
        {history.length > 0 && (
          <span className="text-sm text-neutral-500">{history.length} episodios</span>
        )}
      </div>

      {/* Ad — top */}
      <div className="mb-6 flex justify-center">
        <AdSlot placement="profile_top" />
      </div>

      {history.length === 0 ? (
        <div className="text-center py-16">
          <p className="text-5xl mb-4">🕐</p>
          <p className="text-neutral-400">
            Tu historial está vacío. Los episodios que marques como vistos aparecerán acá.
          </p>
        </div>
      ) : (
        <div className="space-y-6">
          {Array.from(grouped.entries()).map(([date, episodes]) => (
            <div key={date}>
              <h2 className="text-xs font-semibold text-neutral-500 uppercase tracking-wider mb-3 capitalize">
                {date}
              </h2>
              <div className="space-y-2">
                {episodes.map((ep) => (
                  <Link
                    key={ep.episode_id}
                    href={`/series/${ep.series_slug}/${ep.episode_number}`}
                    className="flex items-center gap-3 p-3 rounded-xl bg-neutral-900 border border-neutral-800 hover:border-neutral-600 transition-colors group"
                  >
                    {/* Thumbnail / cover */}
                    <div className="relative w-12 h-16 shrink-0 rounded overflow-hidden bg-neutral-800">
                      {ep.cover_url ? (
                        <Image
                          src={ep.cover_url}
                          alt={ep.series_title}
                          fill
                          sizes="48px"
                          className="object-cover"
                        />
                      ) : (
                        <div className="w-full h-full flex items-center justify-center text-neutral-600 text-lg">🎬</div>
                      )}
                    </div>

                    {/* Info */}
                    <div className="flex-1 min-w-0">
                      <p className="text-sm font-medium text-white truncate group-hover:text-indigo-300 transition-colors">
                        {ep.series_title}
                      </p>
                      <p className="text-xs text-neutral-400">
                        Episodio {ep.episode_number}
                        {ep.episode_title && ` — ${ep.episode_title}`}
                      </p>
                    </div>

                    {/* Time */}
                    <span className="text-xs text-neutral-600 shrink-0">
                      {timeAgo(ep.watched_at)}
                    </span>
                  </Link>
                ))}
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Ad — bottom */}
      <div className="mt-8 flex justify-center">
        <AdSlot placement="profile_bottom" />
      </div>
    </div>
  );
}
