import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { encodeId } from "@/lib/short-id";
import type { TierListSummary } from "@/lib/tierlist";
import CreateTierListButton from "@/components/tierlist/CreateTierListButton";
import DeleteTierListButton from "@/components/tierlist/DeleteTierListButton";
import type { Metadata } from "next";
import Link from "next/link";

export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "Mis Tier Lists — SheicobAnime",
};

async function getUserTierLists(userId: string): Promise<TierListSummary[]> {
  try {
    const db = getDb();
    const { rows } = await db.query(
      `SELECT l.id, l.name, l.is_public, l.created_at, l.updated_at,
              COUNT(e.series_slug)::int AS entry_count
       FROM user_tier_lists l
       LEFT JOIN user_tier_entries e ON e.tier_list_id = l.id
       WHERE l.user_id = $1
       GROUP BY l.id
       ORDER BY l.updated_at DESC`,
      [userId],
    );
    return rows as TierListSummary[];
  } catch {
    return [];
  }
}

export default async function TierListPage() {
  const session = await auth();

  if (!session?.user?.id) {
    return (
      <div className="container mx-auto px-4 py-16 max-w-2xl text-center">
        <p className="text-5xl mb-4">🏆</p>
        <h1 className="text-2xl font-bold text-white mb-2">Iniciá sesión</h1>
        <p className="text-neutral-400 mb-6">
          Necesitás una cuenta para crear y ver tus tier lists.
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

  const lists = await getUserTierLists(session.user.id);

  return (
    <div className="container mx-auto px-4 py-8 max-w-5xl">
      <div className="flex items-center justify-between mb-6 gap-4 flex-wrap">
        <h1 className="text-2xl font-bold text-white">Mis Tier Lists</h1>
        <CreateTierListButton />
      </div>

      {lists.length === 0 ? (
        <div className="text-center py-20">
          <p className="text-5xl mb-4">🏆</p>
          <p className="text-neutral-400 mb-2">No tenés tier lists creadas todavía.</p>
          <p className="text-sm text-neutral-500">
            Creá una para rankear tus animes en S / A / B / C / D / F.
          </p>
        </div>
      ) : (
        <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 gap-4">
          {lists.map((list) => (
            <div key={list.id} className="relative group">
              {/* Delete button — appears on hover */}
              <DeleteTierListButton tierListId={list.id} tierListName={list.name} />

              <Link
                href={`/tierlist/${encodeId(list.id)}`}
                className="flex flex-col bg-neutral-900 border border-neutral-800 rounded-xl overflow-hidden hover:border-neutral-600 transition-all"
              >
                {/* Tier badge preview */}
                <div className="flex h-14 overflow-hidden">
                  {(["S", "A", "B", "C", "D", "F"] as const).map((tier, i) => (
                    <div
                      key={tier}
                      className={`flex-1 flex items-center justify-center text-sm font-extrabold
                        ${tier === "S" ? "bg-red-600 text-white" :
                          tier === "A" ? "bg-orange-500 text-white" :
                          tier === "B" ? "bg-yellow-500 text-neutral-900" :
                          tier === "C" ? "bg-green-600 text-white" :
                          tier === "D" ? "bg-blue-600 text-white" :
                          "bg-neutral-600 text-neutral-200"}`}
                      style={{ borderRight: i < 5 ? "1px solid rgba(0,0,0,0.2)" : undefined }}
                    >
                      {tier}
                    </div>
                  ))}
                </div>

                <div className="p-3 flex flex-col gap-1">
                  <p className="text-sm font-semibold text-white line-clamp-1">{list.name}</p>
                  <div className="flex items-center justify-between mt-1">
                    <span className="text-xs text-neutral-500">
                      {list.entry_count === 0
                        ? "Sin animes"
                        : `${list.entry_count} anime${list.entry_count !== 1 ? "s" : ""}`}
                    </span>
                    <span className="text-xs text-indigo-400 group-hover:text-indigo-300 transition-colors">
                      Ver →
                    </span>
                  </div>
                </div>
              </Link>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
