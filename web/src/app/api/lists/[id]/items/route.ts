import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { NextResponse } from "next/server";

interface RouteContext {
  params: Promise<{ id: string }>;
}

async function verifyOwner(listId: string, userId: string) {
  const db = getDb();
  const { rows } = await db.query(
    `SELECT user_id FROM user_lists WHERE id = $1`,
    [listId],
  );
  if (rows.length === 0) return "not_found";
  if ((rows[0] as { user_id: string }).user_id !== userId) return "forbidden";
  return "ok";
}

// POST /api/lists/[id]/items — add a series to a list
// Body: { seriesSlug: string, seriesTitle: string, coverUrl?: string | null }
export async function POST(req: Request, { params }: RouteContext) {
  const { id } = await params;
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "No autorizado" }, { status: 401 });

  const check = await verifyOwner(id, session.user.id);
  if (check === "not_found")
    return NextResponse.json({ error: "Lista no encontrada" }, { status: 404 });
  if (check === "forbidden")
    return NextResponse.json({ error: "No autorizado" }, { status: 403 });

  const body = (await req.json()) as {
    seriesSlug?: string;
    seriesTitle?: string;
    coverUrl?: string | null;
  };

  if (!body.seriesSlug)
    return NextResponse.json({ error: "seriesSlug es requerido" }, { status: 400 });

  const db = getDb();
  try {
    await db.query(
      `INSERT INTO user_list_items (list_id, series_slug, series_title, cover_url)
       VALUES ($1, $2, $3, $4)
       ON CONFLICT (list_id, series_slug) DO NOTHING`,
      [id, body.seriesSlug, body.seriesTitle ?? "", body.coverUrl ?? null],
    );
    // Bump list updated_at
    await db.query(
      `UPDATE user_lists SET updated_at = now() WHERE id = $1`,
      [id],
    );
    return NextResponse.json({ ok: true });
  } catch {
    return NextResponse.json({ error: "Error al agregar el item" }, { status: 500 });
  }
}

// DELETE /api/lists/[id]/items?slug=xxx — remove a series from a list
export async function DELETE(req: Request, { params }: RouteContext) {
  const { id } = await params;
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "No autorizado" }, { status: 401 });

  const check = await verifyOwner(id, session.user.id);
  if (check === "not_found")
    return NextResponse.json({ error: "Lista no encontrada" }, { status: 404 });
  if (check === "forbidden")
    return NextResponse.json({ error: "No autorizado" }, { status: 403 });

  const url = new URL(req.url);
  const slug = url.searchParams.get("slug");
  if (!slug)
    return NextResponse.json({ error: "slug es requerido" }, { status: 400 });

  const db = getDb();
  try {
    await db.query(
      `DELETE FROM user_list_items WHERE list_id = $1 AND series_slug = $2`,
      [id, slug],
    );
    // Bump list updated_at
    await db.query(
      `UPDATE user_lists SET updated_at = now() WHERE id = $1`,
      [id],
    );
    return NextResponse.json({ deleted: true });
  } catch {
    return NextResponse.json({ error: "Error al quitar el item" }, { status: 500 });
  }
}
