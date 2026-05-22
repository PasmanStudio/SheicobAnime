"use server";

import { auth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { revalidatePath } from "next/cache";

interface UpdateProfileResult {
  ok: boolean;
  error?: string;
}

export async function updateProfile(formData: FormData): Promise<UpdateProfileResult> {
  const session = await auth();
  if (!session?.user?.id) {
    return { ok: false, error: "No autenticado." };
  }

  const name = (formData.get("name") as string | null)?.trim() ?? "";
  const username = (formData.get("username") as string | null)?.trim() ?? "";
  const bio = (formData.get("bio") as string | null)?.trim() ?? "";

  // Basic validation
  if (name.length > 60) return { ok: false, error: "El nombre no puede superar los 60 caracteres." };
  if (username.length > 30) return { ok: false, error: "El nombre de usuario no puede superar los 30 caracteres." };
  if (bio.length > 300) return { ok: false, error: "La bio no puede superar los 300 caracteres." };
  if (username && !/^[a-zA-Z0-9_.-]+$/.test(username)) {
    return { ok: false, error: "El nombre de usuario solo puede contener letras, números, guiones y puntos." };
  }

  try {
    const db = getDb();

    // Check username uniqueness (if changed)
    if (username) {
      const { rows } = await db.query<{ id: string }>(
        `SELECT id FROM users WHERE username = $1 AND id <> $2 LIMIT 1`,
        [username, session.user.id],
      );
      if (rows.length > 0) {
        return { ok: false, error: "Ese nombre de usuario ya está en uso." };
      }
    }

    await db.query(
      `UPDATE users SET name = $1, username = $2, bio = $3 WHERE id = $4`,
      [name || null, username || null, bio || null, session.user.id],
    );

    // Revalidate both the old and new username paths
    revalidatePath(`/usuario/${session.user.id}`);
    if (username) revalidatePath(`/usuario/${username}`);

    return { ok: true };
  } catch (err) {
    console.error("updateProfile error:", err);
    return { ok: false, error: "Error al guardar. Intentá de nuevo." };
  }
}
