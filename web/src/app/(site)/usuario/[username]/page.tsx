import { auth } from "@/lib/auth";
import type { Metadata } from "next";
import Image from "next/image";
import { notFound } from "next/navigation";
import { Pool } from "pg";

// ─── DB helpers ───────────────────────────────────────────────────────────────

let _pool: Pool | null = null;
function getPool(): Pool {
  if (!_pool) {
    _pool = new Pool({
      connectionString: process.env.NEXTAUTH_DATABASE_URL,
      max: 3,
    });
  }
  return _pool;
}

interface DbUser {
  id: string;
  name: string | null;
  email: string | null;
  image: string | null;
  username: string | null;
  bio: string | null;
  created_at: string;
}

async function getUserByUsername(username: string): Promise<DbUser | null> {
  try {
    const pool = getPool();
    const { rows } = await pool.query<DbUser>(
      `SELECT id, name, email, image, username, bio, created_at
       FROM users
       WHERE username = $1
       LIMIT 1`,
      [username],
    );
    return rows[0] ?? null;
  } catch {
    return null;
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
  const [user, session] = await Promise.all([
    getUserByUsername(username),
    auth(),
  ]);

  if (!user) notFound();

  const isOwn = session?.user?.id === user.id;
  const displayName = user.name ?? user.username ?? username;
  const joinedYear = new Date(user.created_at).getFullYear();

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
              <button className="shrink-0 px-4 py-1.5 rounded-lg border border-neutral-700 text-sm text-neutral-300 hover:text-white hover:border-neutral-500 transition-colors">
                Editar perfil
              </button>
            )}
          </div>

          {/* Bio */}
          {user.bio ? (
            <p className="text-sm text-neutral-300 leading-relaxed mb-3">{user.bio}</p>
          ) : isOwn ? (
            <p className="text-sm text-neutral-600 italic mb-3">
              Todavía no agregaste una bio.
            </p>
          ) : null}

          <p className="text-xs text-neutral-600">Miembro desde {joinedYear}</p>
        </div>
      </div>

      {/* Activity sections — placeholders for #129, #130, #131 */}
      <div className="mt-6 grid gap-4">
        <div className="rounded-xl bg-neutral-900 border border-neutral-800 p-5">
          <h2 className="text-sm font-semibold text-white mb-2">Actividad reciente</h2>
          <p className="text-sm text-neutral-500">
            El historial de actividad estará disponible próximamente.
          </p>
        </div>
      </div>
    </div>
  );
}
