// ─── Auth.js v5 (NextAuth) configuration ─────────────────────────────────────
// Docs: https://authjs.dev/getting-started/installation?framework=next.js

import NextAuth from "next-auth";
import Google from "next-auth/providers/google";
import Discord from "next-auth/providers/discord";
import PostgresAdapter from "@auth/pg-adapter";
import { dbFacade } from "@/lib/db";

// dbFacade resolves the right Pool per environment: a per-request pool on
// Cloudflare Workers (sockets can't cross requests) and a global singleton
// on Node. See src/lib/db.ts.
export const { handlers, auth, signIn, signOut } = NextAuth({
  adapter: PostgresAdapter(dbFacade),

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
    // Attach user.id and username to the session so client can use them
    session({ session, user }) {
      if (user?.id) session.user.id = user.id;
      const dbUser = user as { username?: string | null };
      if (dbUser?.username) session.user.username = dbUser.username;
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
