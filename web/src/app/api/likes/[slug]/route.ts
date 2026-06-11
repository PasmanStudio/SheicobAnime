import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { awardXp } from "@/lib/xp";
import { NextResponse } from "next/server";

type Params = { params: Promise<{ slug: string }> };

// GET /api/likes/[slug] — public like count + whether the current user has liked it
export async function GET(_req: Request, { params }: Params) {
  const { slug } = await params;
  const session = await auth();
  const db = getDb();

  try {
    const { rows } = await db.query<{ count: string }>(
      `SELECT COUNT(*)::text AS count FROM user_anime_likes WHERE series_slug = $1`,
      [slug],
    );
    const count = parseInt(rows[0]?.count ?? "0", 10);

    let liked = false;
    if (session?.user?.id) {
      const { rows: likeRows } = await db.query(
        `SELECT 1 FROM user_anime_likes WHERE user_id = $1 AND series_slug = $2`,
        [session.user.id, slug],
      );
      liked = likeRows.length > 0;
    }

    return NextResponse.json({ liked, count }, {
      headers: { "Cache-Control": "no-store" },
    });
  } catch {
    return NextResponse.json({ liked: false, count: 0 });
  }
}

// POST /api/likes/[slug] — toggle like (add if not liked, remove if liked)
// Body: { seriesTitle: string, coverUrl?: string | null }
export async function POST(req: Request, { params }: Params) {
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });

  const { slug } = await params;
  const body = (await req.json()) as { seriesTitle?: string; coverUrl?: string | null };
  const db = getDb();

  try {
    // Check current like state
    const { rows: existing } = await db.query(
      `SELECT 1 FROM user_anime_likes WHERE user_id = $1 AND series_slug = $2`,
      [session.user.id, slug],
    );

    let liked: boolean;
    if (existing.length > 0) {
      // Already liked → unlike
      await db.query(
        `DELETE FROM user_anime_likes WHERE user_id = $1 AND series_slug = $2`,
        [session.user.id, slug],
      );
      liked = false;
    } else {
      // Not liked → like
      await db.query(
        `INSERT INTO user_anime_likes (user_id, series_slug, series_title, cover_url)
         VALUES ($1, $2, $3, $4)
         ON CONFLICT (user_id, series_slug) DO NOTHING`,
        [session.user.id, slug, body.seriesTitle ?? slug, body.coverUrl ?? null],
      );
      liked = true;
      // +1 XP (cap 5/día; dedup por serie — no se farmea con like/unlike)
      await awardXp(session.user.id, "series_liked", `like:${slug}`);
    }

    // Return updated count
    const { rows: countRows } = await db.query<{ count: string }>(
      `SELECT COUNT(*)::text AS count FROM user_anime_likes WHERE series_slug = $1`,
      [slug],
    );
    const count = parseInt(countRows[0]?.count ?? "0", 10);

    return NextResponse.json({ liked, count });
  } catch {
    return NextResponse.json({ error: "Error al procesar el like" }, { status: 500 });
  }
}
