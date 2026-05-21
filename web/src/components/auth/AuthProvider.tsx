"use client";

import { SessionProvider } from "next-auth/react";
import type { ReactNode } from "react";

/**
 * Thin client-side wrapper around next-auth's SessionProvider.
 * Place this inside the server-component layout so all children can access
 * the session via `useSession()`.
 */
export default function AuthProvider({ children }: { children: ReactNode }) {
  return <SessionProvider>{children}</SessionProvider>;
}
