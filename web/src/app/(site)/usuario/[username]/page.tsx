import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { WATCH_STATUS_ICONS, WATCH_STATUS_LABELS, type WatchEntry, type WatchStatus } from "@/lib/watchlist";
import type { ListSummary } from "@/lib/lists";
import type { Metadata } from "next";
import Image from "next/image";
import Link from "next/link";
import { notFound } from "next/navigation";
import ProfileEditModal from "./ProfileEditModal";

// ─── Types ────────────────────────────────────────────────────────────────────

interface DbUser {
  id: string;
  name: string | null;
  email: string | null;
  image: string | null;
  username: string | null;
  bio: string | null;
  created_at: string;
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
    const { rows } = await db.query<DbUser>(
      isUuid
        ? `SELECT id, name, email, image, username, bio, created_at FROM users WHERE id = $1::uuid LIMIT 1`
        : `SELECT id, name, email, image, username, bio, created_at FROM users WHERE username = $1 LIMIT 1`,
      [slug],
    );
    return rows[0] ?? null;
  } catch {
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

export default async function UsuarioPage({ params }: Props) {
  const { username } = await params;
  const [user, session] = await Promise.all([getUserBySlug(username), auth()]);

  if (!user) notFound();

  const isOwn = session?.user?.id === user.id;
  const displayName = user.name ?? user.username ?? username;
  const joinedYear = new Date(user.created_at).getFullYear();

  // Only fetch stats if we know the user exists
  const [stats, recentWatchlist, publicLists] = await Promise.all([
    getUserStats(user.id),
    getRecentWatchlist(user.id),
    getPublicLists(user.id),
  ]);

  return (
    <div className="container mx-auto px-4 py-10 max-w-3xl">
      {/* Profile card */}
      <div className="rounded-2xl bg-neutral-900 border border-neutral-800 overflow-hidden">
        {/* Banner */}
        <div className="h-28 bg-gradient-to-br from-indigo-900/60 via-neutral-800 to-neutral-900" />

        {/* Avatar + info */}
        <div className="px-6 pb-6">
          <div className="flex items-end gap-4 -mt-10 mb-4">
            <div className="ring-4 ring-neutral-900 rounded-full shrink-0">
              {user.image ? (
                <Image
                  src={user.image}
                  alt={displayName}
                  width={80}
                  height={80}
                  className="rounded-full object-cover"
                />
              ) : (
                <div className="w-20 h-20 rounded-full bg-indigo-600 flex items-center justify-center text-white text-2xl font-bold">
                  {displayName.charAt(0).toUpperCase()}
                </div>
              )}
            </div>

            <div className="flex-1 min-w-0 pt-10">
              <h1 className="text-xl font-bold text-white truncate">{displayName}</h1>
              <p className="text-sm text-neutral-500">@{user.username ?? username}</p>
            </div>

            {isOwn && (
              <ProfileEditModal
                currentName={user.name}
                currentUsername={user.username}
                currentBio={user.bio}
              />
            )}
          </div>

          {/* Bio */}
          {user.bio ? (
            <p className="text-sm text-neutral-300 leading-relaxed mb-4">{user.bio}</p>
          ) : isOwn ? (
            <p className="text-sm text-neutral-600 italic mb-4">Todavía no agregaste una bio.</p>
          ) : null}

          {/* Stats row */}
          <div className="flex flex-wrap gap-x-6 gap-y-2 text-sm">
            <div className="text-center">
              <p className="text-white font-bold">{stats.episodesWatched}</p>
              <p className="text-neutral-500 text-xs">Episodios vistos</p>
            </div>
            <div className="text-center">
              <p className="text-white font-bold">{stats.watchlistCount}</p>
              <p className="text-neutral-500 text-xs">En lista</p>
            </div>
            {(Object.entries(stats.statusCounts) as [WatchStatus, number][]).map(([s, c]) => (
              <div key={s} className="text-center">
                <p className="text-white font-bold">{c}</p>
                <p className="text-neutral-500 text-xs">
                  {WATCH_STATUS_ICONS[s]} {WATCH_STATUS_LABELS[s]}
                </p>
              </div>
            ))}
          </div>

          <p className="text-xs text-neutral-600 mt-3">Miembro desde {joinedYear}</p>
        </div>
      </div>

      {/* Quick links (own profile) */}
      {isOwn && (
        <div className="mt-4 flex gap-3">
          <Link
            href="/guardado"
            className="flex-1 text-center px-4 py-2.5 rounded-xl bg-neutral-900 border border-neutral-800 hover:border-neutral-600 text-sm text-neutral-300 hover:text-white transition-colors"
          >
            📋 Mi lista
          </Link>
          <Link
            href="/historial"
            className="flex-1 text-center px-4 py-2.5 rounded-xl bg-neutral-900 border border-neutral-800 hover:border-neutral-600 text-sm text-neutral-300 hover:text-white transition-colors"
          >
            🕐 Historial
          </Link>
        </div>
      )}

      {/* Recent watchlist */}
      {recentWatchlist.length > 0 && (
        <div className="mt-6">
          <div className="flex items-center justify-between mb-3">
            <h2 className="text-sm font-semibold text-white">Lista reciente</h2>
            <Link href={`/guardado`} className="text-xs text-indigo-400 hover:text-indigo-300">
              Ver todo →
            </Link>
          </div>
          <div className="grid grid-cols-3 sm:grid-cols-6 gap-3">
            {recentWatchlist.map((entry) => (
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
                      sizes="120px"
                      className="object-cover group-hover:scale-105 transition-transform duration-300"
                    />
                  ) : (
                    <div className="w-full h-full flex items-center justify-center text-neutral-600">🎬</div>
                  )}
                  <div className="absolute bottom-0 inset-x-0 bg-gradient-to-t from-black/80 p-1.5">
                    <p className="text-[10px] text-neutral-300">
                      {WATCH_STATUS_ICONS[entry.status]} {WATCH_STATUS_LABELS[entry.status]}
                    </p>
                  </div>
                </div>
              </Link>
            ))}
          </div>
        </div>
      )}

      {/* Empty state */}
      {recentWatchlist.length === 0 && (
        <div className="mt-6 rounded-xl bg-neutral-900 border border-neutral-800 p-5 text-center">
          <p className="text-sm text-neutral-500">
            {isOwn
              ? "Aún no guardaste ningún anime. Explorá el directorio y guardá tus favoritos."
              : "Este usuario no tiene actividad pública."}
          </p>
          {isOwn && (
            <Link
              href="/directory"
              className="inline-block mt-3 text-sm text-indigo-400 hover:text-indigo-300"
            >
              Explorar directorio →
            </Link>
          )}
        </div>
      )}

      {/* Public playlists */}
      {publicLists.length > 0 && (
        <div className="mt-6">
          <div className="flex items-center justify-between mb-3">
            <h2 className="text-sm font-semibold text-white">📋 Playlists públicas</h2>
            {isOwn && (
              <Link href="/listas" className="text-xs text-indigo-400 hover:text-indigo-300">
                Administrar →
              </Link>
            )}
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            {publicLists.map((lst) => (
              <Link
                key={lst.id}
                href={`/listas/${lst.id}`}
                className="group flex gap-3 rounded-xl bg-neutral-900 border border-neutral-800 hover:border-neutral-600 transition-all p-3"
              >
                {/* Cover mosaic */}
                <div className="shrink-0 w-16 h-16 rounded-lg overflow-hidden bg-neutral-800 grid grid-cols-2 gap-px">
                  {lst.preview_covers.slice(0, 4).map((url, i) => (
                    <div key={i} className="relative overflow-hidden">
                      <Image src={url} alt="" fill sizes="32px" className="object-cover" />
                    </div>
                  ))}
                  {lst.preview_covers.length === 0 && (
                    <div className="col-span-2 row-span-2 flex items-center justify-center text-neutral-600 text-2xl">
                      📋
                    </div>
                  )}
                </div>
                {/* Info */}
                <div className="flex-1 min-w-0">
                  <p className="text-sm font-medium text-white group-hover:text-indigo-300 transition-colors line-clamp-1">
                    {lst.name}
                  </p>
                  {lst.description && (
                    <p className="text-xs text-neutral-500 line-clamp-1 mt-0.5">{lst.description}</p>
                  )}
                  <div className="flex items-center gap-3 mt-1.5 text-xs text-neutral-600">
                    <span>{lst.item_count} anime{lst.item_count !== 1 ? "s" : ""}</span>
                    <span className="flex items-center gap-1">
                      <svg className="w-3 h-3" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
                        <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z" /><circle cx="12" cy="12" r="3" />
                      </svg>
                      {lst.views.toLocaleString("es-AR")}
                    </span>
                  </div>
                </div>
              </Link>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
