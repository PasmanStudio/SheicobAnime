// Shared Postgres pool for Next.js server-side queries (watchlist, history, profile stats).
// Uses the same connection string as Auth.js.
import { Pool } from "pg";

let _pool: Pool | null = null;

export function getDb(): Pool {
  if (!_pool) {
    _pool = new Pool({
      connectionString: process.env.NEXTAUTH_DATABASE_URL,
      max: 5,
      idleTimeoutMillis: 30_000,
      connectionTimeoutMillis: 5_000,
      // Required for Supabase pooler — same as auth.ts pool
      ssl: process.env.NEXTAUTH_DATABASE_URL
        ? { rejectUnauthorized: false }
        : false,
    });
  }
  return _pool;
}
