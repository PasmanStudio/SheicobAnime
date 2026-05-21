import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { TIERS, TIER_COLORS, type Tier, type TierEntry } from "@/lib/tierlist";
import TierPickerOnEntry from "@/components/tierlist/TierPickerOnEntry";
import RemoveFromTierButton from "@/components/tierlist/RemoveFromTierButton";
import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";

interface Props {
  params: Promise<{ id: string }>;
}

interface TierListRow {
  id: string;
  name: string;
  is_public: boolean;
  user_id: string;
  created_at: string;
  updated_at: string;
}

async function getTierList(id: string): Promise<(TierListRow & { entries: TierEntry[] }) | null> {
  try {
    const db = getDb();
    const { rows } = await db.query<TierListRow>(
      `SELECT id, name, is_public, user_id, created_at, updated_at
       FROM user_tier_lists WHERE id = $1`,
      [id],
    );
    if (rows.length === 0) return null;
    const { rows: entries } = await db.query<TierEntry>(
      `SELECT tier_list_id, series_slug, series_title, cover_url, tier, position, added_at
       FROM user_tier_entries
       WHERE tier_list_id = $1
       ORDER BY tier, position, added_at`,
      [id],
    );
    return { ...rows[0], entries };
  } catch {
    return null;
  }
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { id } = await params;
  const list = await getTierList(id);
  if (!list) return { title: "Tier List — SheicobAnime" };
  return { title: `${list.name} — SheicobAnime` };
}

export default async function TierListDetailPage({ params }: Props) {
  const { id } = await params;
  const [list, session] = await Promise.all([getTierList(id), auth()]);

  if (!list) notFound();
  if (!list.is_public && list.user_id !== session?.user?.id) notFound();

  const isOwner = session?.user?.id === list.user_id;

  // Group entries by tier
  const byTier: Record<Tier, TierEntry[]> = { S: [], A: [], B: [], C: [], D: [], F: [] };
  for (const entry of list.entries) {
    byTier[entry.tier].push(entry);
  }

  const totalEntries = list.entries.length;

  return (
    <div className="container mx-auto px-4 py-8 max-w-5xl">
      {/* Header */}
      <div className="mb-6">
        {isOwner && (
          <Link
            href="/tierlist"
            className="inline-flex items-center gap-1.5 text-xs text-neutral-500 hover:text-neutral-300 transition-colors mb-4"
          >
            ← Mis Tier Lists
          </Link>
        )}
        <div className="flex items-center gap-3 flex-wrap">
          <h1 className="text-2xl font-bold text-white flex-1 min-w-0">{list.name}</h1>
          {list.is_public && (
            <span className="text-xs px-2 py-0.5 rounded-full bg-green-900/40 text-green-400 border border-green-800/50">
              Pública
            </span>
          )}
        </div>
        <p className="text-sm text-neutral-500 mt-1">
          {totalEntries === 0 ? "Sin animes" : `${totalEntries} anime${totalEntries !== 1 ? "s" : ""}`}
          {isOwner && (
            <span className="ml-2 text-neutral-600">
              · Hacé click en un anime para cambiar su tier
            </span>
          )}
        </p>
      </div>

      {/* Tier list grid */}
      {totalEntries === 0 ? (
        <div className="text-center py-20">
          <p className="text-5xl mb-4">🏆</p>
          <p className="text-neutral-400 mb-1">Esta tier list está vacía.</p>
          {isOwner && (
            <p className="text-sm text-neutral-500">
              Andá a cualquier serie y usá el botón{" "}
              <span className="text-neutral-300">🏆 Tier List</span> para agregarla.
            </p>
          )}
        </div>
      ) : (
        {/* overflow-visible so TierPickerOnEntry dropdowns aren't clipped */}
        <div className="rounded-xl border border-neutral-800">
          {TIERS.map((tier, idx) => {
            const entries = byTier[tier];
            const colors = TIER_COLORS[tier];
            const isFirst = idx === 0;
            const isLast = idx === TIERS.length - 1;

            return (
              <div
                key={tier}
                className={`flex min-h-[72px] ${idx < TIERS.length - 1 ? "border-b border-neutral-800" : ""}`}
              >
                {/* Tier label — explicit corner rounding since parent has no overflow-hidden */}
                <div
                  className={`w-14 shrink-0 flex items-center justify-center text-2xl font-extrabold ${colors.bg} ${colors.text} select-none
                    ${isFirst ? "rounded-tl-xl" : ""} ${isLast ? "rounded-bl-xl" : ""}`}
                >
                  {tier}
                </div>

                {/* Entries */}
                <div className={`flex-1 bg-neutral-900 px-3 py-2 flex items-center gap-2 flex-wrap min-h-[72px]
                  ${isFirst ? "rounded-tr-xl" : ""} ${isLast ? "rounded-br-xl" : ""}`}>
                  {entries.length === 0 ? (
                    <span className="text-xs text-neutral-700 italic">Vacío</span>
                  ) : (
                    entries.map((entry) => (
                      <div key={entry.series_slug} className="relative group">
                        {isOwner && (
                          <RemoveFromTierButton
                            tierListId={list.id}
                            seriesSlug={entry.series_slug}
                          />
                        )}
                        {isOwner ? (
                          <TierPickerOnEntry
                            tierListId={list.id}
                            seriesSlug={entry.series_slug}
                            seriesTitle={entry.series_title}
                            coverUrl={entry.cover_url}
                            currentTier={entry.tier}
                          />
                        ) : (
                          <Link
                            href={`/series/${entry.series_slug}`}
                            title={entry.series_title}
                            className="block w-14 rounded overflow-hidden"
                          >
                            {entry.cover_url ? (
                              // eslint-disable-next-line @next/next/no-img-element
                              <img
                                src={entry.cover_url}
                                alt={entry.series_title}
                                className="w-full aspect-[2/3] object-cover hover:opacity-80 transition-opacity"
                              />
                            ) : (
                              <div className="w-full aspect-[2/3] bg-neutral-800 flex items-center justify-center text-neutral-600 text-lg">
                                🎬
                              </div>
                            )}
                          </Link>
                        )}
                      </div>
                    ))
                  )}
                </div>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
