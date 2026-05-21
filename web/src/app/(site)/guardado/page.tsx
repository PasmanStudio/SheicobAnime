import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import {
  WATCH_STATUS_COLORS,
  WATCH_STATUS_ICONS,
  WATCH_STATUS_LABELS,
  WATCH_STATUSES,
  type WatchEntry,
  type WatchStatus,
} from "@/lib/watchlist";
import type { Metadata } from "next";
import Image from "next/image";
import Link from "next/link";

export const metadata: Metadata = {
  title: "Mi lista guardada — SheicobAnime",
};

interface Props {
  searchParams: Promise<{ status?: string }>;
}

async function getWatchlist(userId: string, status: WatchStatus | null): Promise<WatchEntry[]> {
  try {
    const db = getDb();
    const { rows } = status
      ? await db.query<WatchEntry>(
          `SELECT * FROM user_watch_entries WHERE user_id = $1 AND status = $2 ORDER BY updated_at DESC`,
          [userId, status],
        )
      : await db.query<WatchEntry>(
          `SELECT * FROM user_watch_entries WHERE user_id = $1 ORDER BY updated_at DESC`,
          [userId],
        );
    return rows;
  } catch {
    return [];
  }
}

export default async function GuardadoPage({ searchParams }: Props) {
  const session = await auth();
  const { status: statusParam } = await searchParams;
  const activeStatus = WATCH_STATUSES.includes(statusParam as WatchStatus)
    ? (statusParam as WatchStatus)
    : null;

  if (!session?.user?.id) {
    return (
      <div className="container mx-auto px-4 py-16 max-w-2xl text-center">
        <p className="text-5xl mb-4">🔒</p>
        <h1 className="text-2xl font-bold text-white mb-2">Iniciá sesión</h1>
        <p className="text-neutral-400 mb-6">
          Necesitás una cuenta para guardar tu lista de anime.
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

  const entries = await getWatchlist(session.user.id, activeStatus);

  // Count per status for tab badges
  const db = getDb();
  const { rows: counts } = await db.query<{ status: WatchStatus; count: string }>(
    `SELECT status, COUNT(*) as count FROM user_watch_entries WHERE user_id = $1 GROUP BY status`,
    [session.user.id],
  );
  const countMap = Object.fromEntries(counts.map((r) => [r.status, Number(r.count)])) as Record<WatchStatus, number>;
  const totalCount = Object.values(countMap).reduce((a, b) => a + b, 0);

  return (
    <div className="container mx-auto px-4 py-8 max-w-5xl">
      <h1 className="text-2xl font-bold text-white mb-6">Mi lista guardada</h1>

      {/* Status tabs */}
      <div className="flex flex-wrap gap-2 mb-6">
        <Link
          href="/guardado"
          className={`flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-sm font-medium transition-colors border
            ${!activeStatus
              ? "bg-indigo-600/20 border-indigo-600/50 text-indigo-300"
              : "border-neutral-700 text-neutral-400 hover:text-white hover:border-neutral-500"}`}
        >
          Todo
          {totalCount > 0 && (
            <span className="text-xs bg-neutral-700 px-1.5 py-0.5 rounded-full">{totalCount}</span>
          )}
        </Link>
        {WATCH_STATUSES.map((s) => (
          <Link
            key={s}
            href={`/guardado?status=${s}`}
            className={`flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-sm font-medium transition-colors border
              ${activeStatus === s
                ? "bg-indigo-600/20 border-indigo-600/50 text-indigo-300"
                : "border-neutral-700 text-neutral-400 hover:text-white hover:border-neutral-500"}`}
          >
            <span>{WATCH_STATUS_ICONS[s]}</span>
            {WATCH_STATUS_LABELS[s]}
            {countMap[s] > 0 && (
              <span className="text-xs bg-neutral-700 px-1.5 py-0.5 rounded-full">{countMap[s]}</span>
            )}
          </Link>
        ))}
      </div>

      {/* Grid */}
      {entries.length === 0 ? (
        <div className="text-center py-16">
          <p className="text-5xl mb-4">📋</p>
          <p className="text-neutral-400">
            {activeStatus
              ? `No tenés animes marcados como "${WATCH_STATUS_LABELS[activeStatus]}".`
              : "Tu lista está vacía. Andá a cualquier anime y guardalo."}
          </p>
        </div>
      ) : (
        <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 gap-4">
          {entries.map((entry) => (
            <Link
              key={entry.series_slug}
              href={`/series/${entry.series_slug}`}
              className="group relative rounded-xl overflow-hidden bg-neutral-900 border border-neutral-800 hover:border-neutral-600 transition-all"
            >
              <div className="relative aspect-[2/3] bg-neutral-800">
                {entry.cover_url ? (
                  <Image
                    src={entry.cover_url}
                    alt={entry.series_title}
                    fill
                    sizes="(max-width: 640px) 45vw, (max-width: 1024px) 30vw, 200px"
                    className="object-cover group-hover:scale-105 transition-transform duration-300"
                  />
                ) : (
                  <div className="w-full h-full flex items-center justify-center text-neutral-600 text-3xl">
                    🎬
                  </div>
                )}
                {/* Status badge */}
                <div className={`absolute top-2 left-2 flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium border backdrop-blur-sm bg-neutral-900/80 ${WATCH_STATUS_COLORS[entry.status]}`}>
                  {WATCH_STATUS_ICONS[entry.status]} {WATCH_STATUS_LABELS[entry.status]}
                </div>
              </div>
              <div className="p-2">
                <p className="text-xs text-neutral-300 leading-tight line-clamp-2">
                  {entry.series_title}
                </p>
              </div>
            </Link>
          ))}
        </div>
      )}
    </div>
  );
}
