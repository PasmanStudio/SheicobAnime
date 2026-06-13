import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { NextResponse } from "next/server";

export interface NotificationRow {
  id: string;
  type: string;
  title: string;
  body: string | null;
  url: string | null;
  read_at: string | null;
  created_at: string;
}

// GET /api/notifications — últimas notificaciones del usuario + contador no leídas
export async function GET() {
  const session = await auth();
  if (!session?.user?.id) return NextResponse.json({ items: [], unread: 0 });

  try {
    const db = getDb();
    const [items, unread] = await Promise.all([
      db.query<NotificationRow>(
        `SELECT id::text, type, title, body, url, read_at, created_at
         FROM user_notifications
         WHERE user_id = $1
         ORDER BY created_at DESC
         LIMIT 30`,
        [session.user.id],
      ),
      db.query<{ count: string }>(
        `SELECT COUNT(*)::text AS count FROM user_notifications
         WHERE user_id = $1 AND read_at IS NULL`,
        [session.user.id],
      ),
    ]);
    return NextResponse.json(
      { items: items.rows, unread: parseInt(unread.rows[0]?.count ?? "0", 10) },
      { headers: { "Cache-Control": "no-store" } },
    );
  } catch {
    // Schema de engagement no aplicado → la campana se muestra vacía
    return NextResponse.json({ items: [], unread: 0 });
  }
}

// POST /api/notifications — marcar como leídas (todas, o una por id)
// Body: { id?: string }  (sin id = marca todas)
export async function POST(req: Request) {
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });

  const body = (await req.json().catch(() => ({}))) as { id?: string };
  const db = getDb();

  try {
    if (body.id) {
      await db.query(
        `UPDATE user_notifications SET read_at = now()
         WHERE id = $1::bigint AND user_id = $2 AND read_at IS NULL`,
        [body.id, session.user.id],
      );
    } else {
      await db.query(
        `UPDATE user_notifications SET read_at = now()
         WHERE user_id = $1 AND read_at IS NULL`,
        [session.user.id],
      );
    }
    return NextResponse.json({ ok: true });
  } catch {
    return NextResponse.json({ error: "Error" }, { status: 500 });
  }
}
