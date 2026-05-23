import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { encodeId } from "@/lib/short-id";
import type { ListSummary } from "@/lib/lists";
import CreateListButton from "@/components/lists/CreateListButton";
import AdSlot from "@/components/ads/AdSlot";
import type { Metadata } from "next";
import Image from "next/image";
import Link from "next/link";

export const metadata: Metadata = {
  title: "Mis listas — SheicobAnime",
};

async function getUserLists(userId: string): Promise<ListSummary[]> {
  try {
    const db = getDb();
    const { rows } = await db.query(
      `SELECT
        l.id, l.name, l.description, l.is_public, l.created_at, l.updated_at,
        COUNT(i.series_slug)::int AS item_count,
        ARRAY(
          SELECT i2.cover_url FROM user_list_items i2
          WHERE i2.list_id = l.id AND i2.cover_url IS NOT NULL
          ORDER BY i2.added_at DESC LIMIT 3
        ) AS preview_covers
      FROM user_lists l
      LEFT JOIN user_list_items i ON i.list_id = l.id
      WHERE l.user_id = $1
      GROUP BY l.id
      ORDER BY l.updated_at DESC`,
      [userId],
    );
    return rows as ListSummary[];
  } catch {
    return [];
  }
}

export default async function ListasPage() {
  const session = await auth();

  if (!session?.user?.id) {
    return (
      <div className="container mx-auto px-4 py-16 max-w-2xl text-center">
        <p className="text-5xl mb-4">🔒</p>
        <h1 className="text-2xl font-bold text-white mb-2">Iniciá sesión</h1>
        <p className="text-neutral-400 mb-6">
          Necesitás una cuenta para crear y ver tus listas personales.
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

  const lists = await getUserLists(session.user.id);

  return (
    <div className="container mx-auto px-4 py-8 max-w-5xl">
      {/* Header */}
      <div className="flex items-center justify-between mb-6 gap-4 flex-wrap">
        <h1 className="text-2xl font-bold text-white">Mis listas</h1>
        <CreateListButton />
      </div>

      {/* Ad — top */}
      <div className="mb-6 flex justify-center">
        <AdSlot placement="profile_top" />
      </div>

      {/* Grid */}
      {lists.length === 0 ? (
        <div className="text-center py-20">
          <p className="text-5xl mb-4">📋</p>
          <p className="text-neutral-400 mb-2">No tenés listas creadas todavía.</p>
          <p className="text-sm text-neutral-500">
            Creá una lista para organizar tus animes favoritos.
          </p>
        </div>
      ) : (
        <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 gap-4">
          {lists.map((list) => (
            <Link
              key={list.id}
              href={`/listas/${encodeId(list.id)}`}
              className="group flex flex-col bg-neutral-900 border border-neutral-800 rounded-xl overflow-hidden hover:border-neutral-600 transition-all"
            >
              {/* Cover previews */}
              <div className="flex h-28 bg-neutral-800 overflow-hidden">
                {list.preview_covers.length === 0 ? (
                  <div className="w-full flex items-center justify-center text-neutral-600 text-4xl">
                    📋
                  </div>
                ) : list.preview_covers.length === 1 ? (
                  <div className="relative w-full">
                    <Image
                      src={list.preview_covers[0]}
                      alt=""
                      fill
                      sizes="(max-width: 640px) 90vw, 33vw"
                      className="object-cover group-hover:scale-105 transition-transform duration-300"
                    />
                  </div>
                ) : (
                  list.preview_covers.map((url, i) => (
                    <div
                      key={i}
                      className="relative flex-1 overflow-hidden"
                      style={{ borderRight: i < list.preview_covers.length - 1 ? "1px solid rgba(255,255,255,0.05)" : undefined }}
                    >
                      <Image
                        src={url}
                        alt=""
                        fill
                        sizes="(max-width: 640px) 30vw, 11vw"
                        className="object-cover group-hover:scale-105 transition-transform duration-300"
                      />
                    </div>
                  ))
                )}
              </div>

              {/* Info */}
              <div className="p-3 flex-1 flex flex-col gap-1">
                <p className="text-sm font-semibold text-white line-clamp-1">{list.name}</p>
                {list.description && (
                  <p className="text-xs text-neutral-400 line-clamp-2">{list.description}</p>
                )}
                <div className="flex items-center justify-between mt-auto pt-1">
                  <span className="text-xs text-neutral-500">
                    {list.item_count === 0
                      ? "Sin animes"
                      : `${list.item_count} anime${list.item_count !== 1 ? "s" : ""}`}
                  </span>
                  <span className="text-xs text-indigo-400 group-hover:text-indigo-300 transition-colors">
                    Ver lista →
                  </span>
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
