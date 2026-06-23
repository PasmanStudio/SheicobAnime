import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import type { ListDetail } from "@/lib/lists";
import AddSeriesModal from "@/components/lists/AddSeriesModal";
import EditListNameButton from "@/components/lists/EditListNameButton";
import RemoveFromListButton from "@/components/lists/RemoveFromListButton";
import TogglePublicButton from "@/components/lists/TogglePublicButton";
import ShareButtons from "@/components/share/ShareButtons";
import { siteUrl } from "@/lib/site-url";
import ListViewTracker from "./ListViewTracker";
import AdSlot from "@/components/ads/AdSlot";
import { encodeId, decodeId, isUuid } from "@/lib/short-id";
import type { Metadata } from "next";
import Image from "next/image";
import Link from "next/link";
import { notFound, redirect } from "next/navigation";

export const dynamic = "force-dynamic";

interface Props {
  params: Promise<{ id: string }>;
}

class DbError extends Error {}

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
    throw new DbError("db_unavailable");
  }
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { id } = await params;
  const realId = isUuid(id) ? id : (decodeId(id) ?? id);
  const list = await getListDetail(realId);
  if (!list) return { title: "Lista no encontrada — SheicobAnime" };
  return {
    title: `${list.name} — SheicobAnime`,
    description: list.description ?? `Lista con ${list.items.length} animes.`,
  };
}

export default async function ListaDetailPage({ params }: Props) {
  const { id } = await params;

  // Redirect old UUID URLs to short-ID form
  if (isUuid(id)) {
    redirect(`/listas/${encodeId(id)}`);
  }

  // Decode short ID to real UUID for DB lookup
  const realId = decodeId(id);
  if (!realId) notFound();

  let list: Awaited<ReturnType<typeof getListDetail>>;
  let session: import("next-auth").Session | null;
  try {
    [list, session] = await Promise.all([getListDetail(realId), auth()]);
  } catch (err) {
    if (err instanceof DbError) {
      return (
        <div className="container mx-auto px-4 py-20 max-w-xl text-center">
          <p className="text-5xl mb-4">⚠️</p>
          <h1 className="text-xl font-bold text-white mb-2">Error temporal</h1>
          <p className="text-ink-2 mb-6 text-sm">
            No se pudo conectar a la base de datos. Esto es momentáneo, intentá de nuevo en unos segundos.
          </p>
          <Link
            href={`/listas/${id}`}
            className="inline-flex items-center gap-2 px-5 py-2.5 rounded-lg bg-brand text-[var(--text-on-accent)] hover:brightness-110 font-medium transition-colors text-sm"
          >
            Reintentar
          </Link>
        </div>
      );
    }
    throw err;
  }

  if (!list) notFound();

  // Build the canonical share URL once (server-side, uses the short ID form)
  const shareUrl = `${siteUrl()}/listas/${encodeId(list.id)}`;

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
              className="inline-flex items-center gap-1.5 text-xs text-ink-3 hover:text-ink-1 transition-colors"
            >
              ← Mis listas
            </Link>
          ) : (
            <div />
          )}
          {list.is_public && !isOwner && (
            <ShareButtons url={shareUrl} text={`Mira la lista "${list.name}" en SheicobAnime`} />
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
          <p className="text-sm text-ink-2 mb-3">{list.description}</p>
        )}

        {/* Meta + owner actions row */}
        <div className="flex flex-wrap items-center gap-3">
          <span className="text-xs text-ink-3">
            {list.items.length === 0
              ? "Sin animes"
              : `${list.items.length} anime${list.items.length !== 1 ? "s" : ""}`}
          </span>

          {list.is_public ? (
            <span className="flex items-center gap-1 text-xs text-ink-3">
              <svg className="w-3.5 h-3.5" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
                <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z" />
                <circle cx="12" cy="12" r="3" />
              </svg>
              {list.views.toLocaleString("es-AR")} vistas
            </span>
          ) : isOwner ? (
            <span className="text-xs text-ink-3">Solo tú puedes verla</span>
          ) : null}

          {/* Owner actions */}
          {isOwner && (
            <div className="ml-auto flex items-center gap-2">
              <TogglePublicButton listId={list.id} initialIsPublic={list.is_public} />
              {list.is_public && (
                <ShareButtons url={shareUrl} text={`Mira la lista "${list.name}" en SheicobAnime`} />
              )}
            </div>
          )}
        </div>
      </div>

      {/* Ad — top */}
      <div className="my-4 flex justify-center">
        <AdSlot placement="profile_top" />
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
          <p className="text-ink-2 mb-2">Esta lista está vacía.</p>
          {isOwner && (
            <p className="text-sm text-ink-3">
              Usa el botón <span className="text-ink-2">Agregar anime</span> de arriba,
              o ve a cualquier serie y usa el botón{" "}
              <span className="text-ink-2">📋 Listas</span>.
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
                className="flex flex-col rounded-xl overflow-hidden bg-abyss-2 border border-line-1 hover:border-line-2 transition-all"
              >
                <div className="relative aspect-[2/3] bg-abyss-3">
                  {item.cover_url ? (
                    <Image
                      src={item.cover_url}
                      alt={item.series_title}
                      fill
                      sizes="(max-width: 640px) 45vw, (max-width: 1024px) 30vw, 200px"
                      className="object-cover group-hover:scale-105 transition-transform duration-300"
                    />
                  ) : (
                    <div className="w-full h-full flex items-center justify-center text-ink-3 text-3xl">
                      🎬
                    </div>
                  )}
                </div>
                <div className="p-2">
                  <p className="text-xs text-ink-2 leading-tight line-clamp-2">
                    {item.series_title}
                  </p>
                </div>
              </Link>
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
