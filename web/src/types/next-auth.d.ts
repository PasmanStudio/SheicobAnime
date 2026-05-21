// Extend Auth.js session types to include user.id
// See: https://authjs.dev/getting-started/typescript#module-augmentation

import type { DefaultSession } from "next-auth";

declare module "next-auth" {
  interface Session {
    user: {
      id: string;
    } & DefaultSession["user"];
  }
}
