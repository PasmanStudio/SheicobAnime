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

    // 2. Auth tables exist?
    const { rows: tableRows } = await db.query<{ tablename: string }>(
      `SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename = ANY($1)`,
      [["users", "sessions", "accounts", "user_lists"]],
    );
    const tableNames = tableRows.map((r) => r.tablename);
    checks.tableUsers = tableNames.includes("users");
    checks.tableSessions = tableNames.includes("sessions");
    checks.tableAccounts = tableNames.includes("accounts");
    checks.tableUserLists = tableNames.includes("user_lists");

    // 3. user_lists has views column?
    const { rows: colRows } = await db.query(
      `SELECT column_name FROM information_schema.columns
       WHERE table_name = 'user_lists' AND column_name = 'views'`,
    );
    checks.columnListViews = colRows.length > 0;

    // 4. Count rows (non-sensitive counts only)
    const { rows: userCount } = await db.query<{ count: string }>(
      `SELECT COUNT(*)::text AS count FROM users`,
    );
    checks.userCount = Number(userCount[0]?.count ?? 0);

    const { rows: sessionCount } = await db.query<{ count: string }>(
      `SELECT COUNT(*)::text AS count FROM sessions WHERE expires > NOW()`,
    );
    checks.activeSessions = Number(sessionCount[0]?.count ?? 0);

  } catch (err) {
    checks.error = err instanceof Error ? err.message : String(err);
    checks.ping = false;
    return NextResponse.json(checks, { status: 503 });
  }

  const allOk =
    checks.ping === true &&
    checks.tableUsers === true &&
    checks.tableSessions === true;

  return NextResponse.json(checks, { status: allOk ? 200 : 503 });
}
