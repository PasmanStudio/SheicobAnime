import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { randomBytes } from "node:crypto";
import { NextResponse } from "next/server";

// GET /api/digest — estado de suscripción del usuario actual
export async function GET() {
  const session = await auth();
  if (!session?.user?.id) return NextResponse.json({ optIn: false });

  try {
    const db = getDb();
    const { rows } = await db.query<{ email_digest_opt_in: boolean }>(
      `SELECT email_digest_opt_in FROM users WHERE id::text = $1`,
      [session.user.id],
    );
    return NextResponse.json(
      { optIn: Boolean(rows[0]?.email_digest_opt_in) },
      { headers: { "Cache-Control": "no-store" } },
    );
  } catch {
    return NextResponse.json({ optIn: false });
  }
}

// POST /api/digest — toggle del digest semanal por email
export async function POST() {
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });

  try {
    const db = getDb();
    const { rows } = await db.query<{ email_digest_opt_in: boolean }>(
      `UPDATE users
       SET email_digest_opt_in = NOT email_digest_opt_in,
           email_unsub_token = COALESCE(email_unsub_token, $2)
       WHERE id::text = $1
       RETURNING email_digest_opt_in`,
      [session.user.id, randomBytes(24).toString("hex")],
    );
    return NextResponse.json({ optIn: Boolean(rows[0]?.email_digest_opt_in) });
  } catch {
    return NextResponse.json({ error: "Error al actualizar" }, { status: 500 });
  }
}
