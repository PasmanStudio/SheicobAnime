import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { NextResponse } from "next/server";

interface RouteContext {
  params: Promise<{ id: string }>;
}

// GET /api/lists/[id] — fetch list detail with all items
export async function GET(_req: Request, { params }: RouteContext) {
  const { id } = await params;
  const session = await auth();
  const db = getDb();

  try {
    const { rows } = await db.query(
      `SELECT id, name, description, is_public, user_id, created_at, updated_at
       FROM user_lists WHERE id = $1`,
      [id],
    );

    if (rows.length === 0)
      return NextResponse.json({ error: "Lista no encontrada" }, { status: 404 });

    const list = rows[0] as { is_public: boolean; user_id: string };

    // Private list — only owner can view
    if (!list.is_public && list.user_id !== session?.user?.id)
      return NextResponse.json({ error: "Lista no encontrada" }, { status: 404 });

    const { rows: items } = await db.query(
      `SELECT series_slug, series_title, cover_url, added_at
       FROM user_list_items
       WHERE list_id = $1
       ORDER BY added_at DESC`,
      [id],
    );

    return NextResponse.json({ ...rows[0], items });
  } catch {
    return NextResponse.json({ error: "Error interno" }, { status: 500 });
  }
}

// PUT /api/lists/[id] — rename/update list
// Body: { name?: string, description?: string }
export async function PUT(req: Request, { params }: RouteContext) {
  const { id } = await params;
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "No autorizado" }, { status: 401 });

  const db = getDb();

  // Verify ownership
  const { rows } = await db.query(
    `SELECT user_id FROM user_lists WHERE id = $1`,
    [id],
  );
  if (rows.length === 0)
    return NextResponse.json({ error: "Lista no encontrada" }, { status: 404 });
  if ((rows[0] as { user_id: string }).user_id !== session.user.id)
    return NextResponse.json({ error: "No autorizado" }, { status: 403 });

  const body = (await req.json()) as { name?: string; description?: string };
  const name = body.name?.trim();
  if (!name)
    return NextResponse.json({ error: "El nombre es requerido" }, { status: 400 });

  try {
    const { rows: updated } = await db.query(
      `UPDATE user_lists
       SET name = $1, description = $2, updated_at = now()
       WHERE id = $3
       RETURNING id, name, description, is_public, created_at, updated_at`,
      [name, body.description?.trim() ?? null, id],
    );
    return NextResponse.json(updated[0]);
  } catch {
    return NextResponse.json({ error: "Error al actualizar la lista" }, { status: 500 });
  }
}

// DELETE /api/lists/[id] — delete list
export async function DELETE(_req: Request, { params }: RouteContext) {
  const { id } = await params;
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "No autorizado" }, { status: 401 });

  const db = getDb();

  // Verify ownership
  const { rows } = await db.query(
    `SELECT user_id FROM user_lists WHERE id = $1`,
    [id],
  );
  if (rows.length === 0)
    return NextResponse.json({ error: "Lista no encontrada" }, { status: 404 });
  if ((rows[0] as { user_id: string }).user_id !== session.user.id)
    return NextResponse.json({ error: "No autorizado" }, { status: 403 });

  try {
    await db.query(`DELETE FROM user_lists WHERE id = $1`, [id]);
    return NextResponse.json({ deleted: true });
  } catch {
    return NextResponse.json({ error: "Error al eliminar la lista" }, { status: 500 });
  }
}
