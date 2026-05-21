import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { WATCH_STATUSES, type WatchStatus } from "@/lib/watchlist";
import { NextResponse } from "next/server";

// GET /api/watchlist?status=mirando  — list current user's watch entries
export async function GET(req: Request) {
  const session = await auth();
  if (!session?.user?.id) return NextResponse.json([], { status: 200 });

  const url = new URL(req.url);
  const status = url.searchParams.get("status") as WatchStatus | null;

  const db = getDb();
  const { rows } = status
    ? await db.query(
        `SELECT * FROM user_watch_entries
         WHERE user_id = $1 AND status = $2
         ORDER BY updated_at DESC`,
        [session.user.id, status],
      )
    : await db.query(
        `SELECT * FROM user_watch_entries
         WHERE user_id = $1
         ORDER BY updated_at DESC`,
        [session.user.id],
      );

  return NextResponse.json(rows);
}

// PUT /api/watchlist — upsert or delete an entry
// Body: { seriesSlug, seriesTitle, coverUrl, status }  (status=null → delete)
export async function PUT(req: Request) {
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });

  const body = await req.json() as {
    seriesSlug?: string;
    seriesTitle?: string;
    coverUrl?: string | null;
    status?: WatchStatus | null;
  };

  const { seriesSlug, seriesTitle, coverUrl, status } = body;
  if (!seriesSlug)
    return NextResponse.json({ error: "seriesSlug required" }, { status: 400 });

  const db = getDb();

  // null/undefined status → remove from list
  if (!status) {
    await db.query(
      `DELETE FROM user_watch_entries WHERE user_id = $1 AND series_slug = $2`,
      [session.user.id, seriesSlug],
    );
    return NextResponse.json({ deleted: true });
  }

  if (!WATCH_STATUSES.includes(status))
    return NextResponse.json({ error: "Invalid status" }, { status: 400 });

  await db.query(
    `INSERT INTO user_watch_entries (user_id, series_slug, series_title, cover_url, status, created_at, updated_at)
     VALUES ($1, $2, $3, $4, $5, now(), now())
     ON CONFLICT (user_id, series_slug)
     DO UPDATE SET status = EXCLUDED.status,
                   series_title = EXCLUDED.series_title,
                   cover_url = EXCLUDED.cover_url,
                   updated_at = now()`,
    [session.user.id, seriesSlug, seriesTitle ?? "", coverUrl ?? null, status],
  );

  return NextResponse.json({ ok: true, status });
}
