import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { encodeId } from "@/lib/short-id";
import type { TierListSummary } from "@/lib/tierlist";
import CreateTierListButton from "@/components/tierlist/CreateTierListButton";
import DeleteTierListButton from "@/components/tierlist/DeleteTierListButton";
import AdSlot from "@/components/ads/AdSlot";
import SectionHeader from "@/components/ui/SectionHeader";
import TierBadge from "@/components/ui/TierBadge";
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
      <div className="mx-auto max-w-2xl px-4 py-16 text-center">
        <div className="mb-5 flex justify-center gap-1.5">
          {(["S", "A", "B"] as const).map((t) => (
            <TierBadge key={t} tier={t} size={34} />
          ))}
        </div>
        <h1 className="sh-display mb-2 text-2xl">Inicia sesión</h1>
        <p className="mb-1 text-ink-2">Necesitás una cuenta para crear y ver tus tier lists.</p>
        <p className="mb-6 text-sm text-ink-3">
          Entra con tu cuenta y armá tu primera tier list en un minuto.
        </p>
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

  const lists = await getUserTierLists(session.user.id);

  return (
    <div className="mx-auto max-w-5xl px-4 py-8">
      <div className="mb-6 flex flex-wrap items-end justify-between gap-4">
        <SectionHeader size="lg" title="Mis tier lists" eyebrow="Rankeá tus animes" />
        <CreateTierListButton />
      </div>

      {lists.length === 0 ? (
        <div className="py-20 text-center text-sm">
          <div className="mb-5 flex justify-center gap-1.5 opacity-60">
            {(["S", "A", "B", "C", "D"] as const).map((t) => (
              <TierBadge key={t} tier={t} size={28} />
            ))}
          </div>
          <p className="text-ink-2">No tienes tier lists creadas todavía.</p>
          <p className="mt-1 text-ink-3">
            Crea una para rankear tus animes en S / A / B / C / D / F.
          </p>
        </div>
      ) : (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 md:grid-cols-3">
          {lists.map((list) => (
            <div key={list.id} className="group relative">
              {/* Delete button — appears on hover */}
              <DeleteTierListButton tierListId={list.id} tierListName={list.name} />

              <Link
                href={`/tierlist/${encodeId(list.id)}`}
                className="flex flex-col gap-2.5 rounded-card border border-line-1 bg-abyss-2 p-4 transition-all duration-fast hover:-translate-y-0.5 hover:border-line-2 hover:shadow-card"
              >
                {/* Tier badge preview — paralelogramos de 14° */}
                <div className="flex gap-1.5">
                  {(["S", "A", "B", "C", "D"] as const).map((tier) => (
                    <TierBadge key={tier} tier={tier} size={24} />
                  ))}
                </div>

                <p className="sh-title line-clamp-1 text-[15px]">{list.name}</p>
                <div className="flex items-center justify-between">
                  <span className="sh-stat text-[11px] text-ink-3">
                    {list.entry_count === 0
                      ? "Sin animes"
                      : `${list.entry_count} anime${list.entry_count !== 1 ? "s" : ""}`}
                  </span>
                  <span className="text-xs font-semibold text-brand-bright transition-colors duration-fast group-hover:text-[var(--cyan-200)]">
                    Ver →
                  </span>
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
