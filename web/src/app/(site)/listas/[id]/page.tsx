import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import type { ListDetail } from "@/lib/lists";
import AddSeriesModal from "@/components/lists/AddSeriesModal";
import EditListNameButton from "@/components/lists/EditListNameButton";
import RemoveFromListButton from "@/components/lists/RemoveFromListButton";
import TogglePublicButton from "@/components/lists/TogglePublicButton";
import ShareButtons from "./ShareButtons";
import ListViewTracker from "./ListViewTracker";
import type { Metadata } from "next";
import Image from "next/image";
import Link from "next/link";
import { notFound, redirect } from "next/navigation";

export const dynamic = "force-dynamic";

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

    // Fetch views separately — column was added by a later migration;
    // default to 0 so the page never crashes if the column isn't there yet.
    let views = 0;
    try {
      const { rows: vRows } = await db.query(
        `SELECT views FROM user_lists WHERE id = $1`,
        [id],
      );
      views = (vRows[0]?.views as number) ?? 0;
    } catch {
      // views column not yet present — safe fallback to 0
    }

    const { rows: items } = await db.query(
      `SELECT series_slug, series_title, cover_url, added_at
       FROM user_list_items
       WHERE list_id = $1
       ORDER BY added_at DESC`,
      [id],
    );
    return { ...rows[0], views, items } as ListDetail;
  } catch (err) {
    console.error("[getListDetail] Error fetching list:", err);
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
  if (!list.is_public && list.user_id !== session?.user?.id) {
    if (!session?.user?.id) {
      // auth() returned null — private list, unauthenticated → ask to sign in
      redirect(`/?callbackUrl=/listas/${id}`);
    }
    notFound();
  }

  const isOwner = session?.user?.id === list.user_id;

  const existingSlugs = list.items.map((i) => i.series_slug);

  return (
    <div className="container mx-auto px-4 py-8 max-w-5xl">
      {/* View tracker — increments counter once per visit (public lists only) */}
      {list.is_public && !isOwner && <ListViewTracker listId={list.id} />}

      {/* Header */}
      <div className="mb-4">
        {/* Back link row */}
        <div className="flex items-center justify-between mb-3">
          {isOwner ? (
            <Link
              href="/listas"
              className="inline-flex items-center gap-1.5 text-xs text-neutral-500 hover:text-neutral-300 transition-colors"
            >
              ← Mis listas
            </Link>
          ) : (
            <div />
          )}
          {list.is_public && !isOwner && (
            <ShareButtons listId={list.id} listName={list.name} />
          )}
        </div>

        {/* Title row */}
        <div className="flex items-start gap-3 flex-wrap mb-2">
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
          <p className="text-sm text-neutral-400 mb-3">{list.description}</p>
        )}

        {/* Meta + owner actions row */}
        <div className="flex flex-wrap items-center gap-3">
          <span className="text-xs text-neutral-500">
            {list.items.length === 0
              ? "Sin animes"
              : `${list.items.length} anime${list.items.length !== 1 ? "s" : ""}`}
          </span>

          {list.is_public ? (
            <span className="flex items-center gap-1 text-xs text-neutral-500">
              <svg className="w-3.5 h-3.5" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
                <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z" />
                <circle cx="12" cy="12" r="3" />
              </svg>
              {list.views.toLocaleString("es-AR")} vistas
            </span>
          ) : isOwner ? (
            <span className="text-xs text-neutral-600">Solo vos podés verla</span>
          ) : null}

          {/* Owner actions */}
          {isOwner && (
            <div className="ml-auto flex items-center gap-2">
              <TogglePublicButton listId={list.id} initialIsPublic={list.is_public} />
              {list.is_public && (
                <ShareButtons listId={list.id} listName={list.name} />
              )}
            </div>
          )}
        </div>
      </div>

      {/* Owner: add anime button */}
      {isOwner && (
        <div className="mb-6">
          <AddSeriesModal listId={list.id} existingSlugs={existingSlugs} />
        </div>
      )}

      {/* Items grid */}
      {list.items.length === 0 ? (
        <div className="text-center py-20">
          <p className="text-5xl mb-4">📋</p>
          <p className="text-neutral-400 mb-2">Esta lista está vacía.</p>
          {isOwner && (
            <p className="text-sm text-neutral-500">
              Usá el botón <span className="text-neutral-300">Agregar anime</span> de arriba,
              o andá a cualquier serie y usá el botón{" "}
              <span className="text-neutral-300">📋 Listas</span>.
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
