import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import type { EpisodeHistoryEntry } from "@/lib/watchlist";
import AdSlot from "@/components/ads/AdSlot";
import SectionHeader from "@/components/ui/SectionHeader";
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
      <div className="mx-auto max-w-2xl px-4 py-16 text-center">
        <h1 className="sh-display mb-2 text-2xl">Iniciá sesión</h1>
        <p className="mb-1 text-ink-2">Necesitás una cuenta para ver tu historial de episodios.</p>
        <p className="mb-6 text-sm text-ink-3">Entrá con tu cuenta y retomá donde dejaste.</p>
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

  const history = await getHistory(session.user.id);
  const grouped = groupByDate(history);

  return (
    <div className="mx-auto max-w-3xl px-4 py-8">
      <div className="flex items-end justify-between mb-6 gap-4">
        <SectionHeader size="lg" title="Historial" />
        {history.length > 0 && (
          <span className="sh-stat text-sm text-ink-3">{history.length} episodios</span>
        )}
      </div>

      {history.length === 0 ? (
        <div className="py-16 text-center text-sm">
          <p className="text-ink-2">Tu historial está vacío.</p>
          <p className="mt-1 text-ink-3">Los episodios que marques como vistos van a aparecer acá.</p>
        </div>
      ) : (
        <div className="space-y-6">
          {Array.from(grouped.entries()).map(([date, episodes]) => (
            <div key={date}>
              <h2 className="sh-label mb-3 block">{date}</h2>
              <div className="space-y-2">
                {episodes.map((ep) => (
                  <Link
                    key={ep.episode_id}
                    href={`/series/${ep.series_slug}/${ep.episode_number}`}
                    className="group flex items-center gap-3 rounded-card border border-line-1 bg-abyss-2 p-3 transition-colors duration-fast hover:border-line-2 hover:bg-abyss-3"
                  >
                    {/* Thumbnail / cover */}
                    <div className="relative w-12 h-16 shrink-0 rounded-badge overflow-hidden bg-abyss-3">
                      {ep.cover_url ? (
                        <Image
                          src={ep.cover_url}
                          alt={ep.series_title}
                          fill
                          sizes="48px"
                          className="object-cover"
                        />
                      ) : (
                        <div className="flex h-full w-full items-center justify-center font-display italic font-black text-ink-3">
                          {ep.series_title.trim()[0]?.toUpperCase() ?? "?"}
                        </div>
                      )}
                    </div>

                    {/* Info */}
                    <div className="flex-1 min-w-0">
                      <p className="sh-title truncate text-sm transition-colors duration-fast group-hover:text-[var(--cyan-200)]">
                        {ep.series_title}
                      </p>
                      <p className="text-xs text-ink-2">
                        <span className="sh-stat text-[11px] text-brand-bright">
                          EP {String(ep.episode_number).padStart(2, "0")}
                        </span>
                        {ep.episode_title && ` — ${ep.episode_title}`}
                      </p>
                    </div>

                    {/* Time */}
                    <span className="sh-stat shrink-0 text-[11px] text-ink-3">
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
