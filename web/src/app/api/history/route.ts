import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { awardXp, registerStreakDay } from "@/lib/xp";
import { NextResponse } from "next/server";

// GET /api/history?limit=50 — recent episode watch history
export async function GET(req: Request) {
  const session = await auth();
  if (!session?.user?.id) return NextResponse.json([]);

  const url = new URL(req.url);
  const limit = Math.min(Number(url.searchParams.get("limit") ?? 50), 100);

  const db = getDb();
  const { rows } = await db.query(
    `SELECT * FROM user_episode_history
     WHERE user_id = $1
     ORDER BY watched_at DESC
     LIMIT $2`,
    [session.user.id, limit],
  );
  return NextResponse.json(rows);
}

// POST /api/history — mark an episode as watched
// Body: { episodeId, seriesSlug, episodeNumber, episodeTitle?, seriesTitle?, coverUrl? }
export async function POST(req: Request) {
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });

  const body = await req.json() as {
    episodeId?: string;
    seriesSlug?: string;
    episodeNumber?: number;
    episodeTitle?: string | null;
    seriesTitle?: string | null;
    coverUrl?: string | null;
  };

  const { episodeId, seriesSlug, episodeNumber, episodeTitle, seriesTitle, coverUrl } = body;
  if (!episodeId || !seriesSlug || !episodeNumber)
    return NextResponse.json({ error: "Missing required fields" }, { status: 400 });

  const db = getDb();
  await db.query(
    `INSERT INTO user_episode_history
       (user_id, episode_id, series_slug, episode_number, episode_title, series_title, cover_url, watched_at)
     VALUES ($1, $2, $3, $4, $5, $6, $7, now())
     ON CONFLICT (user_id, episode_id) DO UPDATE SET watched_at = now()`,
    [
      session.user.id,
      episodeId,
      seriesSlug,
      episodeNumber,
      episodeTitle ?? null,
      seriesTitle ?? "",
      coverUrl ?? null,
    ],
  );

  // XP + racha (doc 3) — best-effort, dedup por episodio en la DB.
  // El cliente marca "visto" al superar el umbral de reproducción.
  const [xp, streak] = await Promise.all([
    awardXp(session.user.id, "episode_watched", `ep:${episodeId}`),
    registerStreakDay(session.user.id),
  ]);

  return NextResponse.json({ ok: true, xp, streak });
}
