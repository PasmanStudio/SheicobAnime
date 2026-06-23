import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import {
  WATCH_STATUS_LABELS,
  WATCH_STATUSES,
  type WatchEntry,
  type WatchStatus,
} from "@/lib/watchlist";
import AdSlot from "@/components/ads/AdSlot";
import SectionHeader from "@/components/ui/SectionHeader";
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
      <div className="mx-auto max-w-2xl px-4 py-16 text-center">
        <h1 className="sh-display mb-2 text-2xl">Inicia sesión</h1>
        <p className="mb-1 text-ink-2">Necesitás una cuenta para guardar tu lista de anime.</p>
        <p className="mb-6 text-sm text-ink-3">Entra con tu cuenta y empieza a armarla hoy.</p>
        <Link
          href="/"
          className="inline-flex items-center gap-2 rounded-btn px-5 py-2.5 font-bold text-[var(--text-on-accent)] shadow-glow transition-all duration-fast hover:brightness-110 active:scale-[0.97]"
          style={{ background: "var(--grad-action)" }}
        >
          Volver al inicio
        </Link>
      </div>
    );
  }

  const entries = await getWatchlist(session.user.id, activeStatus);

  // Count per status for tab badges
  let countMap: Record<WatchStatus, number> = {} as Record<WatchStatus, number>;
  let totalCount = 0;
  try {
    const db = getDb();
    const { rows: counts } = await db.query<{ status: WatchStatus; count: string }>(
      `SELECT status, COUNT(*) as count FROM user_watch_entries WHERE user_id = $1 GROUP BY status`,
      [session.user.id],
    );
    countMap = Object.fromEntries(counts.map((r) => [r.status, Number(r.count)])) as Record<WatchStatus, number>;
    totalCount = Object.values(countMap).reduce((a, b) => a + b, 0);
  } catch {
    // Non-fatal — tabs render without counts
  }

  const tabClass = (active: boolean) =>
    `flex items-center gap-1.5 px-3 py-1.5 rounded-btn text-sm font-semibold transition-colors duration-fast border ${
      active
        ? "bg-[var(--accent-muted)] border-[var(--accent-border)] text-brand-bright"
        : "border-line-1 bg-abyss-2 text-ink-3 hover:text-ink-1 hover:border-line-2"
    }`;

  return (
    <div className="mx-auto max-w-5xl px-4 py-8">
      <SectionHeader size="lg" title="Mi lista guardada" className="mb-6" />

      {/* Status tabs */}
      <div className="flex flex-wrap gap-2 mb-6">
        <Link href="/guardado" className={tabClass(!activeStatus)}>
          Todo
          {totalCount > 0 && (
            <span className="sh-stat text-[11px] opacity-80">{totalCount}</span>
          )}
        </Link>
        {WATCH_STATUSES.map((s) => (
          <Link key={s} href={`/guardado?status=${s}`} className={tabClass(activeStatus === s)}>
            {WATCH_STATUS_LABELS[s]}
            {countMap[s] > 0 && (
              <span className="sh-stat text-[11px] opacity-80">{countMap[s]}</span>
            )}
          </Link>
        ))}
      </div>

      {/* Grid */}
      {entries.length === 0 ? (
        <div className="py-16 text-center text-sm">
          <p className="text-ink-2">
            {activeStatus
              ? `No tienes animes marcados como "${WATCH_STATUS_LABELS[activeStatus]}".`
              : "Tu lista está vacía."}
          </p>
          <p className="mt-1 text-ink-3">
            Ve a cualquier anime y guárdalo — aparece aquí al instante.
          </p>
        </div>
      ) : (
        <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 gap-4">
          {entries.map((entry) => (
            <Link
              key={entry.series_slug}
              href={`/series/${entry.series_slug}`}
              className="group relative overflow-hidden rounded-card border border-line-1 bg-abyss-2 transition-all duration-fast hover:-translate-y-0.5 hover:border-line-2 hover:shadow-card"
            >
              <div className="relative aspect-[2/3] bg-abyss-3">
                {entry.cover_url ? (
                  <Image
                    src={entry.cover_url}
                    alt={entry.series_title}
                    fill
                    sizes="(max-width: 640px) 45vw, (max-width: 1024px) 30vw, 200px"
                    className="object-cover transition-transform duration-300 group-hover:scale-[1.04]"
                  />
                ) : (
                  <div className="flex h-full w-full items-center justify-center font-display text-3xl italic font-black text-[rgba(255,255,255,0.14)]">
                    {entry.series_title.trim()[0]?.toUpperCase() ?? "?"}
                  </div>
                )}
                {/* Status badge */}
                <div className="absolute left-2 top-2 rounded-badge bg-[rgba(5,7,11,0.78)] px-2 py-[3px] font-mono text-[10px] font-semibold uppercase tracking-[0.08em] text-[var(--cyan-300)] backdrop-blur-[4px]">
                  {WATCH_STATUS_LABELS[entry.status]}
                </div>
                {/* Protección + título sobre la imagen */}
                <div className="sh-protect absolute inset-x-0 bottom-0 px-2.5 pb-2.5 pt-7">
                  <p className="sh-title line-clamp-2 text-[13px] transition-colors duration-fast group-hover:text-[var(--cyan-200)]">
                    {entry.series_title}
                  </p>
                </div>
              </div>
            </Link>
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
