import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { NextResponse } from "next/server";

interface Params { params: Promise<{ id: string }> }

// GET /api/tierlist/[id] — tier list detail + entries
export async function GET(_req: Request, { params }: Params) {
  const { id } = await params;
  const session = await auth();
  const db = getDb();

  try {
    const { rows } = await db.query(
      `SELECT id, name, is_public, user_id, created_at, updated_at
       FROM user_tier_lists WHERE id = $1`,
      [id],
    );
    if (rows.length === 0)
      return NextResponse.json({ error: "No encontrado" }, { status: 404 });

    const list = rows[0] as { is_public: boolean; user_id: string };
    if (!list.is_public && list.user_id !== session?.user?.id)
      return NextResponse.json({ error: "No autorizado" }, { status: 404 });

    const { rows: entries } = await db.query(
      `SELECT tier_list_id, series_slug, series_title, cover_url, tier, position, added_at
       FROM user_tier_entries
       WHERE tier_list_id = $1
       ORDER BY tier, position, added_at`,
      [id],
    );
    return NextResponse.json({ ...rows[0], entries });
  } catch {
    return NextResponse.json({ error: "Error" }, { status: 500 });
  }
}

// PUT /api/tierlist/[id] — rename / toggle public
export async function PUT(req: Request, { params }: Params) {
  const { id } = await params;
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "No autorizado" }, { status: 401 });

  const body = (await req.json()) as { name?: string; is_public?: boolean };
  const db = getDb();

  try {
    const { rows } = await db.query(
      `UPDATE user_tier_lists
       SET name      = COALESCE($1, name),
           is_public = COALESCE($2, is_public),
           updated_at = now()
       WHERE id = $3 AND user_id = $4
       RETURNING id, name, is_public, updated_at`,
      [body.name?.trim() ?? null, body.is_public ?? null, id, session.user.id],
    );
    if (rows.length === 0)
      return NextResponse.json({ error: "No encontrado" }, { status: 404 });
    return NextResponse.json(rows[0]);
  } catch {
    return NextResponse.json({ error: "Error" }, { status: 500 });
  }
}

// DELETE /api/tierlist/[id] — delete tier list
export async function DELETE(_req: Request, { params }: Params) {
  const { id } = await params;
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "No autorizado" }, { status: 401 });

  const db = getDb();
  try {
    await db.query(
      `DELETE FROM user_tier_lists WHERE id = $1 AND user_id = $2`,
      [id, session.user.id],
    );
    return NextResponse.json({ deleted: true });
  } catch {
    return NextResponse.json({ error: "Error" }, { status: 500 });
  }
}
