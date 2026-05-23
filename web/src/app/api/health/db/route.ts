import { getDb } from "@/lib/db";
import { NextResponse } from "next/server";

/**
 * GET /api/health/db
 * Quick diagnostic: checks Supabase connectivity and auth table presence.
 * NOT sensitive (no user data returned) — safe to be public.
 */
export async function GET() {
  const checks: Record<string, string | number | boolean> = {
    timestamp: new Date().toISOString(),
    hasDbUrl: !!process.env.NEXTAUTH_DATABASE_URL,
    hasAuthSecret: !!process.env.AUTH_SECRET,
  };

  try {
    const db = getDb();

    // 1. Basic connectivity
    const { rows: pingRows } = await db.query(`SELECT 1 AS ok`);
    checks.ping = pingRows[0]?.ok === 1;

    // 2. Tables exist?
    const EXPECTED_TABLES = [
      "users", "sessions", "accounts",
      "user_lists", "user_list_items",
      "user_tier_lists", "user_tier_entries",
      "user_watch_entries", "user_episode_history",
    ];
    const { rows: tableRows } = await db.query<{ tablename: string }>(
      `SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename = ANY($1)`,
      [EXPECTED_TABLES],
    );
    const tableNames = new Set(tableRows.map((r) => r.tablename));
    for (const t of EXPECTED_TABLES) {
      checks[`table_${t}`] = tableNames.has(t);
    }

    // 3. Column checks
    const { rows: colRows } = await db.query<{ table_name: string; column_name: string }>(
      `SELECT table_name, column_name FROM information_schema.columns
       WHERE table_name IN ('user_lists', 'user_tier_lists')
         AND column_name IN ('views', 'is_public')`,
    );
    const cols = new Set(colRows.map((r) => `${r.table_name}.${r.column_name}`));
    checks["col_user_lists.views"]          = cols.has("user_lists.views");
    checks["col_user_lists.is_public"]      = cols.has("user_lists.is_public");
    checks["col_user_tier_lists.is_public"] = cols.has("user_tier_lists.is_public");

    // 4. Count rows (non-sensitive counts only)
    const { rows: userCount } = await db.query<{ count: string }>(
      `SELECT COUNT(*)::text AS count FROM users`,
    );
    checks.userCount = Number(userCount[0]?.count ?? 0);

    const { rows: sessionCount } = await db.query<{ count: string }>(
      `SELECT COUNT(*)::text AS count FROM sessions WHERE expires > NOW()`,
    );
    checks.activeSessions = Number(sessionCount[0]?.count ?? 0);

    // 5. Tier list count
    if (tableNames.has("user_tier_lists")) {
      const { rows: tlCount } = await db.query<{ count: string }>(
        `SELECT COUNT(*)::text AS count FROM user_tier_lists`,
      );
      checks.tierListCount = Number(tlCount[0]?.count ?? 0);
    }

  } catch (err) {
    checks.error = err instanceof Error ? err.message : String(err);
    checks.ping = false;
    return NextResponse.json(checks, { status: 503 });
  }

  const allOk =
    checks.ping === true &&
    checks["table_users"] === true &&
    checks["table_sessions"] === true &&
    checks["table_user_tier_lists"] === true;

  return NextResponse.json(checks, { status: allOk ? 200 : 503 });
}
