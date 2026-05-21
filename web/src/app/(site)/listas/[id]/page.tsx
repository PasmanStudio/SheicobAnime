import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import type { ListDetail } from "@/lib/lists";
import EditListNameButton from "@/components/lists/EditListNameButton";
import RemoveFromListButton from "@/components/lists/RemoveFromListButton";
import type { Metadata } from "next";
import Image from "next/image";
import Link from "next/link";
import { notFound } from "next/navigation";

interface Props {
  params: Promise<{ id: string }>;
}

async function getListDetail(id: string): Promise<ListDetail | null> {
  try {
    const db = getDb();
    const { rows } = await db.query(
      `SELECT id, name, description, is_public, user_id, created_at, updated_at
       FROM user_lists WHERE id = $1`,
      [id],
    );
    if (rows.length === 0) return null;
    const list = rows[0] as ListDetail;
    const { rows: items } = await db.query(
      `SELECT series_slug, series_title, cover_url, added_at
       FROM user_list_items
       WHERE list_id = $1
       ORDER BY added_at DESC`,
      [id],
    );
    return { ...list, items };
  } catch {
    return null;
  }
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { id } = await params;
  const list = await getListDetail(id);
  if (!list) return { title: "Lista no encontrada — SheicobAnime" };
  return {
    title: `${list.name} — SheicobAnime`,
    description: list.description ?? `Lista con ${list.items.length} animes.`,
  };
}

export default async function ListaDetailPage({ params }: Props) {
  const { id } = await params;
  const [list, session] = await Promise.all([getListDetail(id), auth()]);

  if (!list) notFound();

  // Private list — only the owner can view
  if (!list.is_public && list.user_id !== session?.user?.id) notFound();

  const isOwner = session?.user?.id === list.user_id;

  return (
    <div className="container mx-auto px-4 py-8 max-w-5xl">
      {/* Header */}
      <div className="mb-6">
        <div className="flex items-start gap-3 flex-wrap mb-1">
          <h1 className="text-2xl font-bold text-white flex-1 min-w-0 break-words">{list.name}</h1>
          {isOwner && (
            <EditListNameButton
              listId={list.id}
              initialName={list.name}
              initialDescription={list.description}
            />
          )}
        </div>
        {list.description && (
          <p className="text-sm text-neutral-400 mb-2">{list.description}</p>
        )}
        <div className="flex items-center gap-3 text-xs text-neutral-500">
          <span>
            {list.items.length === 0
              ? "Sin animes"
              : `${list.items.length} anime${list.items.length !== 1 ? "s" : ""}`}
          </span>
          {list.is_public && (
            <span className="px-2 py-0.5 rounded-full bg-green-900/40 text-green-400 border border-green-800/50">
              Pública
            </span>
          )}
        </div>
      </div>

      {/* Back link */}
      {isOwner && (
        <Link
          href="/listas"
          className="inline-flex items-center gap-1.5 text-xs text-neutral-500 hover:text-neutral-300 transition-colors mb-6"
        >
          ← Mis listas
        </Link>
      )}

      {/* Items grid */}
      {list.items.length === 0 ? (
        <div className="text-center py-20">
          <p className="text-5xl mb-4">📋</p>
          <p className="text-neutral-400 mb-1">Esta lista está vacía.</p>
          {isOwner && (
            <p className="text-sm text-neutral-500">
              Andá a cualquier serie y usá el botón{" "}
              <span className="text-neutral-300">📋 Listas</span> para agregarla acá.
            </p>
          )}
        </div>
      ) : (
        <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 gap-4">
          {list.items.map((item) => (
            <div key={item.series_slug} className="relative group">
              {isOwner && (
                <RemoveFromListButton listId={list.id} seriesSlug={item.series_slug} />
              )}
              <Link
                href={`/series/${item.series_slug}`}
                className="flex flex-col rounded-xl overflow-hidden bg-neutral-900 border border-neutral-800 hover:border-neutral-600 transition-all"
              >
                <div className="relative aspect-[2/3] bg-neutral-800">
                  {item.cover_url ? (
                    <Image
                      src={item.cover_url}
                      alt={item.series_title}
                      fill
                      sizes="(max-width: 640px) 45vw, (max-width: 1024px) 30vw, 200px"
                      className="object-cover group-hover:scale-105 transition-transform duration-300"
                    />
                  ) : (
                    <div className="w-full h-full flex items-center justify-center text-neutral-600 text-3xl">
                      🎬
                    </div>
                  )}
                </div>
                <div className="p-2">
                  <p className="text-xs text-neutral-300 leading-tight line-clamp-2">
                    {item.series_title}
                  </p>
                </div>
              </Link>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
