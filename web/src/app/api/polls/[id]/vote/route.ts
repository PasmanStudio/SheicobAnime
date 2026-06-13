import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { awardXp } from "@/lib/xp";
import { NextResponse } from "next/server";

type Params = { params: Promise<{ id: string }> };

// Nivel 23+ (Senpai) vota con peso ×2 — doc 3.
const WEIGHTED_VOTE_LEVEL = 23;

// POST /api/polls/[id]/vote — votar una opción. 1 voto por encuesta (PK en DB).
// Body: { optionId: string }
export async function POST(req: Request, { params }: Params) {
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });

  const { id: pollId } = await params;
  const body = (await req.json().catch(() => ({}))) as { optionId?: string };
  if (!body.optionId)
    return NextResponse.json({ error: "Falta optionId" }, { status: 400 });

  const db = getDb();
  try {
    // Validar que la encuesta está vigente y la opción le pertenece
    const valid = await db.query(
      `SELECT 1
       FROM season_polls p
       JOIN season_poll_options o ON o.poll_id = p.id
       WHERE p.id = $1::bigint AND o.id = $2::bigint
         AND now() BETWEEN p.starts_at AND p.ends_at`,
      [pollId, body.optionId],
    );
    if (valid.rows.length === 0)
      return NextResponse.json({ error: "Encuesta u opción inválida" }, { status: 400 });

    // Peso del voto según nivel
    const lvl = await db.query<{ level: number }>(
      `SELECT COALESCE(level, 1) AS level FROM users WHERE id::text = $1`,
      [session.user.id],
    );
    const weight = (lvl.rows[0]?.level ?? 1) >= WEIGHTED_VOTE_LEVEL ? 2 : 1;

    // 1 voto por encuesta (PK poll_id+user_id) — el ON CONFLICT permite cambiar el voto
    await db.query(
      `INSERT INTO season_poll_votes (poll_id, user_id, option_id, weight)
       VALUES ($1::bigint, $2, $3::bigint, $4)
       ON CONFLICT (poll_id, user_id)
       DO UPDATE SET option_id = EXCLUDED.option_id, weight = EXCLUDED.weight, created_at = now()`,
      [pollId, session.user.id, body.optionId, weight],
    );

    // +10 XP por votar (dedup por encuesta — cambiar el voto no re-otorga)
    await awardXp(session.user.id, "poll_voted", `poll:${pollId}`);

    return NextResponse.json({ ok: true, weight });
  } catch {
    return NextResponse.json({ error: "Error al votar" }, { status: 500 });
  }
}
