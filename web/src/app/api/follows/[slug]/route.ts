import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { NextResponse } from "next/server";

type Params = { params: Promise<{ slug: string }> };

// GET /api/follows/[slug] — ¿el usuario sigue esta serie?
export async function GET(_req: Request, { params }: Params) {
  const { slug } = await params;
  const session = await auth();
  if (!session?.user?.id) return NextResponse.json({ following: false });

  try {
    const db = getDb();
    const { rows } = await db.query(
      `SELECT 1 FROM series_follows WHERE user_id = $1 AND series_slug = $2 AND notify`,
      [session.user.id, slug],
    );
    return NextResponse.json(
      { following: rows.length > 0 },
      { headers: { "Cache-Control": "no-store" } },
    );
  } catch {
    return NextResponse.json({ following: false });
  }
}

// POST /api/follows/[slug] — toggle seguir serie (opt-in de avisos de episodios)
export async function POST(_req: Request, { params }: Params) {
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });

  const { slug } = await params;
  const db = getDb();

  try {
    const { rows: existing } = await db.query(
      `SELECT notify FROM series_follows WHERE user_id = $1 AND series_slug = $2`,
      [session.user.id, slug],
    );

    let following: boolean;
    if (existing.length > 0 && existing[0].notify) {
      await db.query(
        `DELETE FROM series_follows WHERE user_id = $1 AND series_slug = $2`,
        [session.user.id, slug],
      );
      following = false;
    } else {
      await db.query(
        `INSERT INTO series_follows (user_id, series_slug, notify)
         VALUES ($1, $2, TRUE)
         ON CONFLICT (user_id, series_slug) DO UPDATE SET notify = TRUE`,
        [session.user.id, slug],
      );
      following = true;
    }

    return NextResponse.json({ following });
  } catch {
    return NextResponse.json({ error: "Error al seguir la serie" }, { status: 500 });
  }
}
