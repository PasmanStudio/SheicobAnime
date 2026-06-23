import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { encodeId } from "@/lib/short-id";
import type { ListSummary } from "@/lib/lists";
import CreateListButton from "@/components/lists/CreateListButton";
import AdSlot from "@/components/ads/AdSlot";
import SectionHeader from "@/components/ui/SectionHeader";
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
      <div className="mx-auto max-w-2xl px-4 py-16 text-center">
        <h1 className="sh-display mb-2 text-2xl">Inicia sesión</h1>
        <p className="mb-1 text-ink-2">Necesitás una cuenta para crear y ver tus listas personales.</p>
        <p className="mb-6 text-sm text-ink-3">Entra con tu cuenta y armá tu primera lista en un minuto.</p>
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

  const lists = await getUserLists(session.user.id);

  return (
    <div className="mx-auto max-w-5xl px-4 py-8">
      {/* Header */}
      <div className="flex items-end justify-between mb-6 gap-4 flex-wrap">
        <SectionHeader size="lg" title="Mis listas" />
        <CreateListButton />
      </div>

      {/* Grid */}
      {lists.length === 0 ? (
        <div className="py-20 text-center text-sm">
          <p className="text-ink-2">No tienes listas creadas todavía.</p>
          <p className="mt-1 text-ink-3">Crea una para organizar tus animes favoritos.</p>
        </div>
      ) : (
        <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 gap-4">
          {lists.map((list) => (
            <Link
              key={list.id}
              href={`/listas/${encodeId(list.id)}`}
              className="group flex flex-col overflow-hidden rounded-card border border-line-1 bg-abyss-2 transition-all duration-fast hover:-translate-y-0.5 hover:border-line-2 hover:shadow-card"
            >
              {/* Cover previews */}
              <div className="flex h-28 bg-abyss-3 overflow-hidden">
                {list.preview_covers.length === 0 ? (
                  <div className="w-full flex items-center justify-center font-display text-3xl italic font-black text-[rgba(255,255,255,0.14)]">
                    {list.name.trim()[0]?.toUpperCase() ?? "?"}
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
                <p className="sh-title line-clamp-1 text-sm">{list.name}</p>
                {list.description && (
                  <p className="text-xs text-ink-2 line-clamp-2">{list.description}</p>
                )}
                <div className="flex items-center justify-between mt-auto pt-1">
                  <span className="sh-stat text-[11px] text-ink-3">
                    {list.item_count === 0
                      ? "Sin animes"
                      : `${list.item_count} anime${list.item_count !== 1 ? "s" : ""}`}
                  </span>
                  <span className="text-xs font-semibold text-brand-bright transition-colors duration-fast group-hover:text-[var(--cyan-200)]">
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
