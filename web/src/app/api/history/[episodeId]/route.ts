import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { NextResponse } from "next/server";

// DELETE /api/history/[episodeId] — remove an episode from watch history
export async function DELETE(
  _req: Request,
  { params }: { params: Promise<{ episodeId: string }> },
) {
  const session = await auth();
  if (!session?.user?.id)
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });

  const { episodeId } = await params;
  const db = getDb();
  await db.query(
    `DELETE FROM user_episode_history WHERE user_id = $1 AND episode_id = $2`,
    [session.user.id, episodeId],
  );
  return NextResponse.json({ deleted: true });
}
