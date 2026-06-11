// Motor de XP (doc 3 — plan de engagement).
// Wrappers finos sobre las funciones SQL award_xp / register_streak_day
// (creadas por db/engagement-schema.sql / migración AddEngagementTables).
//
// Best-effort por diseño: si el schema de engagement todavía no está
// aplicado en la DB, estas funciones loguean y devuelven 0 — nunca rompen
// el endpoint que las llama. El XP es un extra, no parte del flujo crítico.

import { getDb } from "@/lib/db";

export type XpAction =
  | "episode_watched"
  | "series_completed"
  | "comment_posted"
  | "comment_like_received"
  | "series_liked"
  | "tierlist_published"
  | "poll_voted";

/**
 * Otorga XP por una acción. Caps diarios y dedup por ref los garantiza
 * la función SQL. Devuelve el XP efectivamente otorgado (0 si cap/duplicado).
 */
export async function awardXp(
  userId: string,
  action: XpAction,
  refId?: string,
): Promise<number> {
  try {
    const db = getDb();
    const { rows } = await db.query<{ award_xp: number }>(
      `SELECT award_xp($1, $2, $3) AS award_xp`,
      [userId, action, refId ?? null],
    );
    return rows[0]?.award_xp ?? 0;
  } catch (err) {
    console.warn("[xp] award_xp falló (schema de engagement aplicado?):", err);
    return 0;
  }
}

export interface StreakResult {
  streak: number;
  bonusXp: number;
  graceUsed: boolean;
}

/**
 * Registra el día de racha del usuario (idempotente por día).
 * Llamar cuando un episodio cuenta como visto (>= 60%).
 */
export async function registerStreakDay(userId: string): Promise<StreakResult | null> {
  try {
    const db = getDb();
    const { rows } = await db.query<{
      streak: number;
      bonus_xp: number;
      grace_used: boolean;
    }>(`SELECT * FROM register_streak_day($1)`, [userId]);
    const r = rows[0];
    if (!r) return null;
    return { streak: r.streak, bonusXp: r.bonus_xp, graceUsed: r.grace_used };
  } catch (err) {
    console.warn("[xp] register_streak_day falló:", err);
    return null;
  }
}
