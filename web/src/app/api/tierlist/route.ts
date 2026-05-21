import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { TIERS, type Tier } from "@/lib/tierlist";
import { NextResponse } from "next/server";

// GET /api/tierlist              → user's tier lists with entry count
// GET /api/tierlist?seriesSlug=x → also returns in_tier_lists: Record<listId, Tier>
export async function GET(req: Request) {
  const session = await auth();
  if (!session?.user?.id) return NextResponse.json([], { status: 200 });

  const url = new URL(req.url);
  const seriesSlug = url.searchParams.get("seriesSlug");

  const db = getDb();
  try {
    const { rows } = await db.query(
      `SELECT l.id, l.name, l.is_public, l.created_at, l.updated_at,
              COUNT(e.series_slug)::int AS entry_count
       FROM user_tier_lists l
       LEFT JOIN user_tier_entries e ON e.tier_list_id = l.id
       WHERE l.user_id = $1
       GROUP BY l.id
       ORDER BY l.updated_at DESC`,
      [session.user.id],
    );

    if (seriesSlug) {
      const { rows: inRows } = await db.query(
        `SELECT tier_list_id, tier FROM user_tier_entries
         WHERE series_slug = $1
           AND tier_list_id IN (SELECT id FROM user_tier_lists WHERE user_id = $2)`,
        [seriesSlug, session.user.id],
      );
      const inTierLists: Record<string, Tier> = {};
      for (const r of inRows as { tier_list_id: string; tier: Tier }[]) {
        inTierLists[r.tier_list_id] = r.tier;
      }
      return NextResponse.json({ lists: rows, in_tier_lists: inTierLists });
    }

    return NextResponse.json(rows);
  } catch {
    return NextResponse.json([], { status: 200 });
  }
}

// POST /api/tierlist — create a tier list
// Body: { name: string }
export async function POST(req: Request) {
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "No autorizado" }, { status: 401 });

  const body = (await req.json()) as { name?: string };
  const name = body.name?.trim();
  if (!name || name.length > 80)
    return NextResponse.json({ error: "Nombre inválido" }, { status: 400 });

  const db = getDb();
  try {
    const { rows } = await db.query(
      `INSERT INTO user_tier_lists (user_id, name)
       VALUES ($1, $2)
       RETURNING id, name, is_public, created_at, updated_at`,
      [session.user.id, name],
    );
    return NextResponse.json(rows[0], { status: 201 });
  } catch {
    return NextResponse.json({ error: "Error al crear" }, { status: 500 });
  }
}

// Suppress unused import warning — TIERS used for validation in entries route
void TIERS;
