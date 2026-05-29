import { getDb } from "@/lib/db";
import { NextResponse } from "next/server";

export interface RankingEntry {
  series_slug: string;
  series_title: string;
  cover_url: string | null;
  like_count: number;
}

// GET /api/ranking — top 50 series by like count (public, cached 60s)
export async function GET() {
  const db = getDb();

  try {
    const { rows } = await db.query<{
      series_slug: string;
      series_title: string;
      cover_url: string | null;
      like_count: string;
    }>(
      `SELECT
        series_slug,
        series_title,
        cover_url,
        COUNT(*)::text AS like_count
       FROM user_anime_likes
       GROUP BY series_slug, series_title, cover_url
       ORDER BY like_count DESC, series_title ASC
       LIMIT 50`,
    );

    const data: RankingEntry[] = rows.map((r) => ({
      series_slug: r.series_slug,
      series_title: r.series_title,
      cover_url: r.cover_url,
      like_count: parseInt(r.like_count, 10),
    }));

    return NextResponse.json(data, {
      headers: { "Cache-Control": "public, s-maxage=60, stale-while-revalidate=120" },
    });
  } catch {
    return NextResponse.json([], { status: 200 });
  }
}
