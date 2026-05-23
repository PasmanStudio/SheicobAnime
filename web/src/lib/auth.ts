// ─── Auth.js v5 (NextAuth) configuration ─────────────────────────────────────
// Docs: https://authjs.dev/getting-started/installation?framework=next.js

import NextAuth from "next-auth";
import Google from "next-auth/providers/google";
import Discord from "next-auth/providers/discord";
import PostgresAdapter from "@auth/pg-adapter";
import { Pool } from "pg";

// Create a singleton Pool so we don't exhaust connections.
// connectionString may be undefined during `next build` — Pool only
// throws at query time, not construction time, so the build succeeds.
// ssl: rejectUnauthorized:false is required for Supabase/Railway/Render poolers
// that use self-signed or intermediate certs (avoids SELF_SIGNED_CERT_IN_CHAIN).
const pool = new Pool({
  connectionString: process.env.NEXTAUTH_DATABASE_URL,
  max: 5,
  idleTimeoutMillis: 30_000,
  connectionTimeoutMillis: 5_000,
  ssl: process.env.NEXTAUTH_DATABASE_URL
    ? { rejectUnauthorized: false }
    : false,
});

export const { handlers, auth, signIn, signOut } = NextAuth({
  adapter: PostgresAdapter(pool),

  providers: [
    Google({
      clientId: process.env.AUTH_GOOGLE_ID,
      clientSecret: process.env.AUTH_GOOGLE_SECRET,
    }),
    Discord({
      clientId: process.env.AUTH_DISCORD_ID,
      clientSecret: process.env.AUTH_DISCORD_SECRET,
    }),
  ],

  session: { strategy: "database" },

  pages: {
    signIn: "/",       // redirect to home — login is a modal, not a page
    error: "/",        // auth errors: redirect home (modal handles display)
  },

  callbacks: {
    // Attach user.id to the session so client can use it
    session({ session, user }) {
      if (user?.id) session.user.id = user.id;
      return session;
    },
  },

  events: {
    // Log auth events to Vercel function logs so we can diagnose issues
    async signIn({ user, account, isNewUser }) {
      console.log(`[Auth] signIn: userId=${user?.id} provider=${account?.provider} isNew=${isNewUser}`);
    },
    async signOut(message) {
      const token = "token" in message ? message.token : message.session;
      console.log(`[Auth] signOut: token=${JSON.stringify(token)?.slice(0, 40)}`);
    },
    async createUser({ user }) {
      console.log(`[Auth] createUser: userId=${user.id} email=${user.email}`);
    },
    async session({ session }) {
      console.log(`[Auth] session: userId=${session?.user?.id} expires=${session?.expires}`);
    },
  },
});
