import { getDb } from "@/lib/db";
import { NextResponse } from "next/server";

// GET /api/digest/unsubscribe?token=... — un click para desuscribir (link del email)
export async function GET(req: Request) {
  const token = new URL(req.url).searchParams.get("token");
  if (!token) {
    return new NextResponse("Falta el token.", { status: 400 });
  }

  try {
    const db = getDb();
    const { rowCount } = await db.query(
      `UPDATE users SET email_digest_opt_in = FALSE WHERE email_unsub_token = $1`,
      [token],
    );
    if (rowCount === 0) {
      return new NextResponse("Link inválido o ya usado.", { status: 404 });
    }
  } catch {
    return new NextResponse("Error al desuscribir — probá de nuevo.", { status: 500 });
  }

  return new NextResponse(
    "Listo, te desuscribimos del digest semanal. Podés volver a activarlo desde tu perfil.",
    { status: 200, headers: { "Content-Type": "text/plain; charset=utf-8" } },
  );
}
