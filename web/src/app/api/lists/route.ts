import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { NextResponse } from "next/server";

// GET /api/lists              → returns user's lists with item count and preview covers
// GET /api/lists?seriesSlug=x → also returns in_lists (list IDs containing the series)
export async function GET(req: Request) {
  const session = await auth();
  if (!session?.user?.id) return NextResponse.json([], { status: 200 });

  const url = new URL(req.url);
  const seriesSlug = url.searchParams.get("seriesSlug");

  const db = getDb();

  try {
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
      [session.user.id],
    );

    if (seriesSlug) {
      // Also return which lists already contain this series
      const { rows: inRows } = await db.query(
        `SELECT list_id FROM user_list_items
         WHERE series_slug = $1
         AND list_id IN (SELECT id FROM user_lists WHERE user_id = $2)`,
        [seriesSlug, session.user.id],
      );
      const inLists = inRows.map((r: { list_id: string }) => r.list_id);
      return NextResponse.json({ lists: rows, in_lists: inLists });
    }

    return NextResponse.json(rows);
  } catch {
    return NextResponse.json([], { status: 200 });
  }
}

// POST /api/lists — create a new list
// Body: { name: string, description?: string }
export async function POST(req: Request) {
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "No autorizado" }, { status: 401 });

  const body = (await req.json()) as { name?: string; description?: string };
  const name = body.name?.trim();
  if (!name)
    return NextResponse.json({ error: "El nombre es requerido" }, { status: 400 });

  const db = getDb();
  try {
    const { rows } = await db.query(
      `INSERT INTO user_lists (user_id, name, description)
       VALUES ($1, $2, $3)
       RETURNING id, name, description, is_public, created_at, updated_at`,
      [session.user.id, name, body.description?.trim() ?? null],
    );
    return NextResponse.json(rows[0], { status: 201 });
  } catch {
    return NextResponse.json({ error: "Error al crear la lista" }, { status: 500 });
  }
}
