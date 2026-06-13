import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { NextResponse } from "next/server";

export interface PollOption {
  id: string;
  series_slug: string;
  series_title: string;
  cover_url: string | null;
  votes: number;
}

export interface ActivePoll {
  id: string;
  slug: string;
  title: string;
  ends_at: string;
  options: PollOption[];
  totalVotes: number;
  myVote: string | null;
}

// GET /api/polls/active — encuesta de temporada vigente (now entre starts/ends)
export async function GET() {
  try {
    const db = getDb();
    const session = await auth();

    const pollRes = await db.query<{ id: string; slug: string; title: string; ends_at: string }>(
      `SELECT id::text, slug, title, ends_at
       FROM season_polls
       WHERE now() BETWEEN starts_at AND ends_at
       ORDER BY starts_at DESC
       LIMIT 1`,
    );
    const poll = pollRes.rows[0];
    if (!poll) return NextResponse.json({ poll: null });

    const optsRes = await db.query<PollOption>(
      `SELECT o.id::text, o.series_slug, o.series_title, o.cover_url,
              COALESCE(SUM(v.weight), 0)::int AS votes
       FROM season_poll_options o
       LEFT JOIN season_poll_votes v ON v.option_id = o.id
       WHERE o.poll_id = $1::bigint
       GROUP BY o.id
       ORDER BY o.sort, votes DESC`,
      [poll.id],
    );

    let myVote: string | null = null;
    if (session?.user?.id) {
      const mv = await db.query<{ option_id: string }>(
        `SELECT option_id::text FROM season_poll_votes WHERE poll_id = $1::bigint AND user_id = $2`,
        [poll.id, session.user.id],
      );
      myVote = mv.rows[0]?.option_id ?? null;
    }

    const totalVotes = optsRes.rows.reduce((a, o) => a + o.votes, 0);
    const result: ActivePoll = {
      ...poll,
      options: optsRes.rows,
      totalVotes,
      myVote,
    };
    return NextResponse.json(
      { poll: result },
      { headers: { "Cache-Control": "no-store" } },
    );
  } catch {
    // Schema de engagement no aplicado o sin encuesta activa
    return NextResponse.json({ poll: null });
  }
}
