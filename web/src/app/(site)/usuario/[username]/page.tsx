import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { encodeId } from "@/lib/short-id";
import { WATCH_STATUS_LABELS, type WatchEntry, type WatchStatus } from "@/lib/watchlist";
import type { ListSummary } from "@/lib/lists";
import type { Metadata } from "next";
import Image from "next/image";
import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import ProfileEditModal from "./ProfileEditModal";
import AdSlot from "@/components/ads/AdSlot";
import ProgressBar from "@/components/ui/ProgressBar";
import SectionHeader from "@/components/ui/SectionHeader";

// ─── Types ────────────────────────────────────────────────────────────────────

interface DbUser {
  id: string;
  name: string | null;
  email: string | null;
  image: string | null;
  username: string | null;
  bio: string | null;
  created_at: string;
  xp_total: number;
  level: number;
  streak_days: number;
}

interface BadgeRow {
  code: string;
  name: string;
  description: string;
  group_name: string;
  earned_at: string | null;
}

interface UserStats {
  watchlistCount: number;
  episodesWatched: number;
  statusCounts: Partial<Record<WatchStatus, number>>;
}

// ─── DB helpers ───────────────────────────────────────────────────────────────

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

async function getUserBySlug(slug: string): Promise<DbUser | null> {
  try {
    const db = getDb();
    // Accept both UUID (new links) and username slug (future/existing usernames)
    const isUuid = UUID_RE.test(slug);
    const cols = `id, name, email, image, username, bio, created_at,
                  COALESCE(xp_total, 0) AS xp_total,
                  COALESCE(level, 1) AS level,
                  COALESCE(streak_days, 0) AS streak_days`;
    const { rows } = await db.query<DbUser>(
      isUuid
        ? `SELECT ${cols} FROM users WHERE id = $1::uuid LIMIT 1`
        : `SELECT ${cols} FROM users WHERE username = $1 LIMIT 1`,
      [slug],
    );
    return rows[0] ?? null;
  } catch (err) {
    console.error("[getUserBySlug] DB error for slug=%s:", slug, err);
    return null;
  }
}

async function getUserStats(userId: string): Promise<UserStats> {
  try {
    const db = getDb();
    const [watchlistRes, historyRes, statusRes] = await Promise.all([
      db.query<{ count: string }>(
        `SELECT COUNT(*) as count FROM user_watch_entries WHERE user_id = $1`,
        [userId],
      ),
      db.query<{ count: string }>(
        `SELECT COUNT(*) as count FROM user_episode_history WHERE user_id = $1`,
        [userId],
      ),
      db.query<{ status: WatchStatus; count: string }>(
        `SELECT status, COUNT(*) as count FROM user_watch_entries WHERE user_id = $1 GROUP BY status`,
        [userId],
      ),
    ]);
    return {
      watchlistCount: Number(watchlistRes.rows[0]?.count ?? 0),
      episodesWatched: Number(historyRes.rows[0]?.count ?? 0),
      statusCounts: Object.fromEntries(statusRes.rows.map((r) => [r.status, Number(r.count)])),
    };
  } catch {
    return { watchlistCount: 0, episodesWatched: 0, statusCounts: {} };
  }
}

async function getRecentWatchlist(userId: string, limit = 6): Promise<WatchEntry[]> {
  try {
    const db = getDb();
    const { rows } = await db.query<WatchEntry>(
      `SELECT * FROM user_watch_entries WHERE user_id = $1 ORDER BY updated_at DESC LIMIT $2`,
      [userId, limit],
    );
    return rows;
  } catch {
    return [];
  }
}

async function getBadges(userId: string): Promise<BadgeRow[]> {
  try {
    const db = getDb();
    const { rows } = await db.query<BadgeRow>(
      `SELECT b.code, b.name, b.description, b.group_name, ub.earned_at
       FROM badges b
       LEFT JOIN user_badges ub ON ub.badge_code = b.code AND ub.user_id = $1
       ORDER BY b.sort`,
      [userId],
    );
    return rows;
  } catch {
    // Schema de engagement no aplicado todavía — el perfil funciona igual
    return [];
  }
}

// Fórmula del doc 3: XP acumulado para llegar al nivel n = 250·n·(n−1)/2
function xpForLevel(n: number): number {
  return (250 * n * (n - 1)) / 2;
}

async function getPublicLists(userId: string): Promise<ListSummary[]> {
  try {
    const db = getDb();
    const { rows } = await db.query<ListSummary>(
      `SELECT l.id, l.name, l.description, l.is_public, l.views,
              l.created_at, l.updated_at,
              COUNT(i.list_id)::int AS item_count,
              ARRAY(
                SELECT i2.cover_url FROM user_list_items i2
                WHERE i2.list_id = l.id AND i2.cover_url IS NOT NULL
                ORDER BY i2.added_at DESC LIMIT 3
              ) AS preview_covers
       FROM user_lists l
       LEFT JOIN user_list_items i ON i.list_id = l.id
       WHERE l.user_id = $1 AND l.is_public = true
       GROUP BY l.id
       ORDER BY l.views DESC, l.updated_at DESC
       LIMIT 6`,
      [userId],
    );
    return rows;
  } catch {
    return [];
  }
}

// ─── Page ─────────────────────────────────────────────────────────────────────

interface Props {
  params: Promise<{ username: string }>;
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { username } = await params;
  return {
    title: `@${username} — SheicobAnime`,
    description: `Perfil de ${username} en SheicobAnime.`,
  };
}

const MONTH_NAMES = [
  "enero", "febrero", "marzo", "abril", "mayo", "junio",
  "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre",
];

export default async function UsuarioPage({ params }: Props) {
  const { username } = await params;
  const [user, session] = await Promise.all([getUserBySlug(username), auth()]);

  if (!user) notFound();

  // Redirect UUID-based profile URLs to the username form
  if (UUID_RE.test(username) && user.username) {
    redirect(`/usuario/${user.username}`);
  }

  const isOwn = session?.user?.id === user.id;
  const displayName = user.name ?? user.username ?? username;
  const joined = new Date(user.created_at);
  const joinedLabel = `${MONTH_NAMES[joined.getMonth()]} ${joined.getFullYear()}`;

  // Only fetch stats if we know the user exists
  const [stats, recentWatchlist, publicLists, badges] = await Promise.all([
    getUserStats(user.id),
    getRecentWatchlist(user.id),
    getPublicLists(user.id),
    getBadges(user.id),
  ]);

  // Progreso de XP hacia el próximo nivel (fórmula del doc 3)
  const levelFloor = xpForLevel(user.level);
  const levelCeil = xpForLevel(user.level + 1);
  const xpProgress =
    levelCeil > levelFloor
      ? Math.max(0, Math.min(1, (user.xp_total - levelFloor) / (levelCeil - levelFloor)))
      : 0;
  const earnedCount = badges.filter((b) => b.earned_at !== null).length;

  const statCards: Array<[string, number]> = [
    ["Episodios vistos", stats.episodesWatched],
    ["En lista", stats.watchlistCount],
    ...(Object.entries(stats.statusCounts) as [WatchStatus, number][]).map(
      ([s, c]) => [WATCH_STATUS_LABELS[s], c] as [string, number],
    ),
  ];

  return (
    <>
      {/* ── Cabecera de perfil — banda elevada con speedlines ── */}
      <div className="sh-speedlines border-b border-line-1 bg-abyss-1">
        <div className="mx-auto max-w-container flex flex-wrap items-center gap-6 px-5 pt-9 pb-7">
          {/* Avatar con borde cian + glow */}
          <div
            className="shrink-0 rounded-full"
            style={{ border: "2px solid var(--accent-border)", boxShadow: "var(--glow-accent)" }}
          >
            {user.image ? (
              <Image
                src={user.image}
                alt={displayName}
                width={96}
                height={96}
                className="h-24 w-24 rounded-full object-cover"
              />
            ) : (
              <div
                className="flex h-24 w-24 items-center justify-center rounded-full font-display text-3xl font-bold text-[#EAF6FF]"
                style={{ background: "linear-gradient(135deg, hsl(197,55%,38%), hsl(221,60%,24%))" }}
              >
                {displayName.charAt(0).toUpperCase()}
              </div>
            )}
          </div>

          <div className="flex min-w-[260px] flex-1 flex-col gap-2">
            <div className="flex flex-wrap items-center gap-3">
              <h1 className="sh-display m-0 !text-[clamp(24px,3vw,34px)]">{displayName}</h1>
              <span className="rounded-badge border border-[var(--accent-border)] bg-[var(--accent-muted)] px-2 py-[3px] font-mono text-[11px] font-bold tabular-nums text-brand-bright">
                NIVEL {user.level}
              </span>
            </div>
            <span className="text-[13px] text-ink-3">
              Miembro desde {joinedLabel} ·{" "}
              <span className="text-ink-2">@{user.username ?? username}</span>
            </span>
            {user.bio ? (
              <p className="sh-body m-0 max-w-xl text-sm">{user.bio}</p>
            ) : isOwn ? (
              <p className="m-0 text-sm italic text-ink-3">Todavía no agregaste una bio.</p>
            ) : null}

            {/* XP hacia el próximo nivel */}
            <div className="mt-1 flex max-w-[420px] items-center gap-3">
              <ProgressBar value={xpProgress} height={6} className="flex-1" />
              <span className="sh-stat whitespace-nowrap text-[11px] text-ink-2">
                {user.xp_total.toLocaleString("es-AR")} / {levelCeil.toLocaleString("es-AR")} XP
              </span>
            </div>

            {/* Racha */}
            {user.streak_days > 0 && (
              <div className="mt-0.5 flex items-center gap-2">
                <svg
                  width="16"
                  height="16"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="var(--warning)"
                  strokeWidth="2"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  aria-hidden
                >
                  <path d="M8.5 14.5A2.5 2.5 0 0 0 11 12c0-1.38-.5-2-1-3-1.072-2.143-.224-4.054 2-6 .5 2.5 2 4.9 4 6.5 2 1.6 3 3.5 3 5.5a7 7 0 1 1-14 0c0-1.153.433-2.294 1-3a2.5 2.5 0 0 0 2.5 2.5z" />
                </svg>
                <span className="text-[13px] text-ink-2">
                  Racha de{" "}
                  <b className="sh-stat" style={{ color: "var(--warning)" }}>
                    {user.streak_days} día{user.streak_days !== 1 ? "s" : ""}
                  </b>{" "}
                  mirando anime
                </span>
              </div>
            )}
          </div>

          {isOwn && (
            <ProfileEditModal
              currentName={user.name}
              currentUsername={user.username}
              currentBio={user.bio}
            />
          )}
        </div>

        {/* Stats en mono */}
        <div className="mx-auto max-w-container flex flex-wrap gap-3 px-5 pb-7">
          {statCards.map(([k, v]) => (
            <div
              key={k}
              className="flex-1 basis-[150px] rounded-card border border-line-1 bg-abyss-2 px-4 py-3.5"
            >
              <div className="sh-stat text-[22px] text-brand-bright">
                {v.toLocaleString("es-AR")}
              </div>
              <div className="mt-0.5 text-xs text-ink-3">{k}</div>
            </div>
          ))}
        </div>
      </div>

      <div className="mx-auto max-w-container px-5 py-6">
        {/* Quick links (own profile) */}
        {isOwn && (
          <div className="mb-8 flex flex-wrap gap-3">
            {[
              ["/guardado", "Mi lista"],
              ["/historial", "Historial"],
              ["/listas", "Playlists"],
              ["/tierlist", "Tier lists"],
            ].map(([href, label]) => (
              <Link
                key={href}
                href={href}
                className="flex-1 basis-[140px] rounded-btn border border-line-1 bg-abyss-2 px-4 py-2.5 text-center text-sm font-semibold text-ink-2 transition-all duration-fast hover:border-line-2 hover:text-ink-1"
              >
                {label}
              </Link>
            ))}
          </div>
        )}

        {/* Recent watchlist */}
        {recentWatchlist.length > 0 && (
          <section className="mb-8">
            <SectionHeader title="Lista reciente" action="Ver todo" actionHref="/guardado" className="mb-4" />
            <div className="grid grid-cols-3 gap-3 sm:grid-cols-6">
              {recentWatchlist.map((entry) => (
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
                        sizes="120px"
                        className="object-cover transition-transform duration-300 group-hover:scale-[1.04]"
                      />
                    ) : (
                      <div className="flex h-full w-full items-center justify-center font-display text-2xl italic font-black text-[rgba(255,255,255,0.14)]">
                        {entry.series_title.trim()[0]?.toUpperCase() ?? "?"}
                      </div>
                    )}
                    <div className="sh-protect absolute inset-x-0 bottom-0 px-1.5 pb-1.5 pt-5">
                      <p className="sh-label !text-[9px] !tracking-[0.08em]">
                        {WATCH_STATUS_LABELS[entry.status]}
                      </p>
                    </div>
                  </div>
                </Link>
              ))}
            </div>
          </section>
        )}

        {/* Empty state */}
        {recentWatchlist.length === 0 && (
          <div className="mb-8 rounded-card border border-line-1 bg-abyss-2 p-5 text-center text-sm">
            <p className="text-ink-2">
              {isOwn
                ? "Todavía no guardaste ningún anime."
                : "Este usuario no tiene actividad pública."}
            </p>
            {isOwn && (
              <p className="mt-1 text-ink-3">
                Explorá el{" "}
                <Link href="/directory" className="font-semibold text-brand-bright hover:text-[var(--cyan-200)]">
                  directorio
                </Link>{" "}
                y guardá tus favoritos.
              </p>
            )}
          </div>
        )}

        {/* Public playlists */}
        {publicLists.length > 0 && (
          <section className="mb-8">
            <SectionHeader
              title="Playlists públicas"
              action={isOwn ? "Administrar" : undefined}
              actionHref={isOwn ? "/listas" : undefined}
              className="mb-4"
            />
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              {publicLists.map((lst) => (
                <Link
                  key={lst.id}
                  href={`/listas/${encodeId(lst.id)}`}
                  className="group flex gap-3 rounded-card border border-line-1 bg-abyss-2 p-3 transition-all duration-fast hover:border-line-2 hover:shadow-card"
                >
                  {/* Cover mosaic */}
                  <div className="grid h-16 w-16 shrink-0 grid-cols-2 gap-px overflow-hidden rounded-btn bg-abyss-3">
                    {lst.preview_covers.slice(0, 4).map((url, i) => (
                      <div key={i} className="relative overflow-hidden">
                        <Image src={url} alt="" fill sizes="32px" className="object-cover" />
                      </div>
                    ))}
                    {lst.preview_covers.length === 0 && (
                      <div className="col-span-2 row-span-2 flex items-center justify-center font-display italic font-black text-ink-3">
                        {lst.name.trim()[0]?.toUpperCase() ?? "?"}
                      </div>
                    )}
                  </div>
                  {/* Info */}
                  <div className="min-w-0 flex-1">
                    <p className="sh-title line-clamp-1 text-sm transition-colors duration-fast group-hover:text-[var(--cyan-200)]">
                      {lst.name}
                    </p>
                    {lst.description && (
                      <p className="mt-0.5 line-clamp-1 text-xs text-ink-3">{lst.description}</p>
                    )}
                    <div className="mt-1.5 flex items-center gap-3">
                      <span className="sh-stat text-[11px] text-ink-3">
                        {lst.item_count} anime{lst.item_count !== 1 ? "s" : ""}
                      </span>
                      <span className="sh-stat flex items-center gap-1 text-[11px] text-ink-3">
                        <svg className="h-3 w-3" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
                          <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z" /><circle cx="12" cy="12" r="3" />
                        </svg>
                        {lst.views.toLocaleString("es-AR")}
                      </span>
                    </div>
                  </div>
                </Link>
              ))}
            </div>
          </section>
        )}

        {/* Badges — mostrar los no-ganados ES la mecánica: deseo visible */}
        {badges.length > 0 && (
          <section className="mb-8">
            <SectionHeader
              title="Badges"
              eyebrow={`${earnedCount} de ${badges.length} ganados`}
              className="mb-4"
            />
            <div className="grid max-w-[980px] grid-cols-1 gap-3.5 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
              {badges.map((b) => {
                const earned = b.earned_at !== null;
                return (
                  <div
                    key={b.code}
                    className={`flex items-center gap-3 rounded-card border bg-abyss-2 p-3.5 ${
                      earned ? "border-[var(--accent-border)]" : "border-line-1 opacity-45"
                    }`}
                  >
                    {/* Paralelogramo de 14° — como TierBadge */}
                    <span
                      className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg"
                      style={{
                        transform: "skewX(-14deg)",
                        background: earned ? "var(--accent-muted)" : "var(--bg-3)",
                        color: earned ? "var(--cyan-300)" : "var(--text-3)",
                      }}
                    >
                      <span
                        className="inline-flex font-display text-base font-black italic"
                        style={{ transform: "skewX(14deg)" }}
                      >
                        {b.name.trim()[0]?.toUpperCase()}
                      </span>
                    </span>
                    <div className="min-w-0">
                      <div className="text-[13px] font-bold text-ink-1">{b.name}</div>
                      <div className="mt-0.5 text-[11px] text-ink-3">{b.description}</div>
                    </div>
                  </div>
                );
              })}
            </div>
          </section>
        )}

        {/* Ad — bottom (el único del perfil; doc 2 mató profile_top) */}
        <div className="flex justify-center">
          <AdSlot placement="profile_bottom" />
        </div>
      </div>
    </>
  );
}
