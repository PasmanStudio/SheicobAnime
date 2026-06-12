import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { NextResponse } from "next/server";

// POST /api/push — guardar suscripción Web Push del navegador
// Body: { endpoint, keys: { p256dh, auth } }
export async function POST(req: Request) {
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });

  const body = (await req.json()) as {
    endpoint?: string;
    keys?: { p256dh?: string; auth?: string };
  };

  if (!body.endpoint || !body.keys?.p256dh || !body.keys?.auth)
    return NextResponse.json({ error: "Suscripción inválida" }, { status: 400 });

  const db = getDb();
  await db.query(
    `INSERT INTO push_subscriptions (user_id, endpoint, p256dh, auth, user_agent)
     VALUES ($1, $2, $3, $4, $5)
     ON CONFLICT (endpoint) DO UPDATE
       SET user_id = EXCLUDED.user_id,
           p256dh = EXCLUDED.p256dh,
           auth = EXCLUDED.auth`,
    [
      session.user.id,
      body.endpoint,
      body.keys.p256dh,
      body.keys.auth,
      req.headers.get("user-agent")?.slice(0, 255) ?? null,
    ],
  );

  return NextResponse.json({ ok: true });
}

// DELETE /api/push — borrar suscripción (logout / opt-out)
// Body: { endpoint }
export async function DELETE(req: Request) {
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });

  const body = (await req.json()) as { endpoint?: string };
  if (!body.endpoint)
    return NextResponse.json({ error: "Falta endpoint" }, { status: 400 });

  const db = getDb();
  await db.query(
    `DELETE FROM push_subscriptions WHERE endpoint = $1 AND user_id = $2`,
    [body.endpoint, session.user.id],
  );

  return NextResponse.json({ ok: true });
}
