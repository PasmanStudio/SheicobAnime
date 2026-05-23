/**
 * UUID ↔ compact short-ID codec.
 *
 * Current format (v2): UUID without dashes → 32-char lowercase hex.
 * Pure string manipulation — no Buffer, no encoding libraries,
 * works identically on server, client, and Edge runtime.
 *
 * Example:
 *   encodeId("10cb974e-9d21-4de4-ae49-31b00d687d3e") → "10cb974e9d214de4ae4931b00d687d3e"
 *   decodeId("10cb974e9d214de4ae4931b00d687d3e")     → "10cb974e-9d21-4de4-ae49-31b00d687d3e"
 *
 * Legacy (v1): 22-char base64url (brief deployment window).
 * decodeId handles these transparently so old shared links still resolve.
 * encodeId never produces base64url anymore.
 */

const UUID_RE  = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const HEX32_RE = /^[0-9a-f]{32}$/i;
// Legacy v1 format produced by the brief base64url deployment
const B64URL_RE = /^[A-Za-z0-9_-]{22}$/;

export function isUuid(s: string): boolean {
  return UUID_RE.test(s);
}

/**
 * UUID → 32-char hex short ID (removes dashes, lowercases).
 */
export function encodeId(uuid: string): string {
  return uuid.replace(/-/g, "").toLowerCase();
}

/**
 * Short ID (any supported format) → UUID string, or null for invalid input.
 *
 * Accepted formats:
 *   - Full UUID with dashes (passthrough — handles old bookmark links)
 *   - 32-char hex (current v2 format)
 *   - 22-char base64url (legacy v1 — keeps old shared links alive)
 */
export function decodeId(shortId: string): string | null {
  // Full UUID passthrough
  if (isUuid(shortId)) return shortId.toLowerCase();

  // v2: 32-char hex
  if (HEX32_RE.test(shortId)) {
    const s = shortId.toLowerCase();
    return `${s.slice(0, 8)}-${s.slice(8, 12)}-${s.slice(12, 16)}-${s.slice(16, 20)}-${s.slice(20)}`;
  }

  // v1 legacy: 22-char base64url (uses atob — available in Node 16+ and browsers)
  if (B64URL_RE.test(shortId)) {
    try {
      // base64url → base64 (swap chars, add padding to reach multiple of 4)
      const b64 = shortId.replace(/-/g, "+").replace(/_/g, "/") + "==";
      const binary = atob(b64);
      if (binary.length < 16) return null;
      const hex = Array.from(binary.slice(0, 16))
        .map((c) => c.charCodeAt(0).toString(16).padStart(2, "0"))
        .join("");
      return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
    } catch {
      return null;
    }
  }

  return null;
}
