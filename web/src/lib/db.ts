// Shared Postgres pool for Next.js server-side queries (watchlist, history, profile stats).
// Uses the same connection string as Auth.js.
//
// On Cloudflare Workers, TCP sockets cannot be shared across requests — a
// module-global Pool hangs the worker as soon as a second request touches a
// socket opened by the first. We detect the per-request Cloudflare context
// (set by the OpenNext adapter) and key one short-lived Pool per request,
// falling back to the classic global singleton on Node (Vercel / next dev).
import { Pool } from "pg";

function makePool(opts: { max: number; idleTimeoutMillis: number }): Pool {
  return new Pool({
    connectionString: process.env.NEXTAUTH_DATABASE_URL,
    connectionTimeoutMillis: 5_000,
    ...opts,
    // Required for Supabase pooler — same as auth.ts pool.
    // sslmode=disable opts out: Workers' nodejs_compat TLS bridge can't
    // upgrade pg sockets, so the Cloudflare deployment connects plaintext.
    ssl:
      process.env.NEXTAUTH_DATABASE_URL &&
      !process.env.NEXTAUTH_DATABASE_URL.includes("sslmode=disable")
        ? { rejectUnauthorized: false }
        : false,
  });
}

let _pool: Pool | null = null;
const poolsByRequest = new WeakMap<object, Pool>();

/** The OpenNext Cloudflare adapter stores {env, ctx, cf} here per request. */
function cloudflareRequestKey(): object | null {
  const cfContext = (
    globalThis as Record<symbol, { ctx?: object } | undefined>
  )[Symbol.for("__cloudflare-context__")];
  return cfContext?.ctx ?? null;
}

export function getDb(): Pool {
  const requestKey = cloudflareRequestKey();
  if (requestKey) {
    let pool = poolsByRequest.get(requestKey);
    if (!pool) {
      // Short idle timeout: the Supabase pooler reaps leftovers server-side
      pool = makePool({ max: 2, idleTimeoutMillis: 2_000 });
      poolsByRequest.set(requestKey, pool);
    }
    return pool;
  }
  if (!_pool) {
    _pool = makePool({ max: 5, idleTimeoutMillis: 30_000 });
  }
  return _pool;
}

/**
 * Pool-shaped facade that re-resolves the per-request pool on every query.
 * Needed by long-lived consumers (the Auth.js adapter) that capture their
 * "pool" once at module scope.
 */
export const dbFacade = {
  query: (...args: Parameters<Pool["query"]>) =>
    (getDb().query as (...a: unknown[]) => unknown)(...args),
} as unknown as Pool;
