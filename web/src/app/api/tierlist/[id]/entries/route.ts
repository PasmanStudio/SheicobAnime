import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { TIERS, type Tier } from "@/lib/tierlist";
import { NextResponse } from "next/server";

interface Params { params: Promise<{ id: string }> }

// POST /api/tierlist/[id]/entries — upsert entry
// Body: { seriesSlug, seriesTitle, coverUrl?, tier }
export async function POST(req: Request, { params }: Params) {
  const { id } = await params;
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "No autorizado" }, { status: 401 });

  const body = (await req.json()) as {
    seriesSlug?: string;
    seriesTitle?: string;
    coverUrl?: string | null;
    tier?: string;
  };

  if (!body.seriesSlug)
    return NextResponse.json({ error: "seriesSlug requerido" }, { status: 400 });
  if (!body.tier || !TIERS.includes(body.tier as Tier))
    return NextResponse.json({ error: "tier inválido" }, { status: 400 });

  const db = getDb();
  try {
    // Verify ownership
    const { rows: listRows } = await db.query(
      `SELECT id FROM user_tier_lists WHERE id = $1 AND user_id = $2`,
      [id, session.user.id],
    );
    if (listRows.length === 0)
      return NextResponse.json({ error: "No encontrado" }, { status: 404 });

    await db.query(
      `INSERT INTO user_tier_entries (tier_list_id, series_slug, series_title, cover_url, tier)
       VALUES ($1, $2, $3, $4, $5)
       ON CONFLICT (tier_list_id, series_slug)
       DO UPDATE SET tier         = EXCLUDED.tier,
                     series_title = EXCLUDED.series_title,
                     cover_url    = COALESCE(EXCLUDED.cover_url, user_tier_entries.cover_url)`,
      [id, body.seriesSlug, body.seriesTitle ?? "", body.coverUrl ?? null, body.tier],
    );

    // Bump updated_at on parent list
    await db.query(
      `UPDATE user_tier_lists SET updated_at = now() WHERE id = $1`,
      [id],
    );

    return NextResponse.json({ ok: true, tier: body.tier });
  } catch {
    return NextResponse.json({ error: "Error" }, { status: 500 });
  }
}

// DELETE /api/tierlist/[id]/entries?slug=xxx — remove entry
export async function DELETE(req: Request, { params }: Params) {
  const { id } = await params;
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "No autorizado" }, { status: 401 });

  const slug = new URL(req.url).searchParams.get("slug");
  if (!slug)
    return NextResponse.json({ error: "slug requerido" }, { status: 400 });

  const db = getDb();
  try {
    const { rows: listRows } = await db.query(
      `SELECT id FROM user_tier_lists WHERE id = $1 AND user_id = $2`,
      [id, session.user.id],
    );
    if (listRows.length === 0)
      return NextResponse.json({ error: "No encontrado" }, { status: 404 });

    await db.query(
      `DELETE FROM user_tier_entries WHERE tier_list_id = $1 AND series_slug = $2`,
      [id, slug],
    );
    return NextResponse.json({ deleted: true });
  } catch {
    return NextResponse.json({ error: "Error" }, { status: 500 });
  }
}
