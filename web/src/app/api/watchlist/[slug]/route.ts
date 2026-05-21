import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { NextResponse } from "next/server";

// GET /api/watchlist/[slug] — get watchlist status for a specific series
export async function GET(
  _req: Request,
  { params }: { params: Promise<{ slug: string }> },
) {
  const session = await auth();
  if (!session?.user?.id) return NextResponse.json(null);

  const { slug } = await params;
  const db = getDb();
  const { rows } = await db.query(
    `SELECT status FROM user_watch_entries WHERE user_id = $1 AND series_slug = $2`,
    [session.user.id, slug],
  );
  return NextResponse.json(rows[0] ?? null);
}
