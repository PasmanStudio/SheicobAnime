"use server";

import { getDb } from "@/lib/db";

/** Increment the view counter for a public list. Fire-and-forget — never throws. */
export async function trackListView(listId: string): Promise<void> {
  try {
    const db = getDb();
    await db.query(
      `UPDATE user_lists SET views = views + 1 WHERE id = $1 AND is_public = true`,
      [listId],
    );
  } catch {
    // Silently ignore — view tracking is best-effort
  }
}
