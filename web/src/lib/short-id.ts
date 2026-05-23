/**
 * UUID ↔ Base64url short ID codec.
 *
 * Converts a standard UUID (36 chars with dashes) to a URL-safe 22-char string
 * and back. Fully reversible — no database changes required.
 *
 * Example:
 *   encodeId("10cb974e-9d21-4de4-ae49-31b00d687d3e") → "EMuXTpIhTeSubRt39l0N4g"
 *   decodeId("EMuXTpIhTeSubRt39l0N4g")               → "10cb974e-9d21-4de4-ae49-31b00d687d3e"
 */

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
// Base64url short IDs are exactly 22 chars (16 bytes → 22 base64url chars, no padding)
const SHORT_ID_RE = /^[A-Za-z0-9_-]{22}$/;

export function isUuid(s: string): boolean {
  return UUID_RE.test(s);
}

export function isShortId(s: string): boolean {
  return SHORT_ID_RE.test(s);
}

/**
 * UUID → 22-char base64url string.
 * Safe to use in URLs without any encoding.
 */
export function encodeId(uuid: string): string {
  const hex = uuid.replace(/-/g, "");
  const bytes = Buffer.from(hex, "hex");
  return bytes.toString("base64url");
}

/**
 * 22-char base64url string → UUID.
 * Returns the input unchanged if it is already a UUID (passthrough).
 * Returns null if the input is neither a valid short ID nor a UUID.
 */
export function decodeId(shortId: string): string | null {
  if (isUuid(shortId)) return shortId;
  if (!isShortId(shortId)) return null;
  try {
    const bytes = Buffer.from(shortId, "base64url");
    if (bytes.length !== 16) return null;
    const hex = bytes.toString("hex");
    return [
      hex.slice(0, 8),
      hex.slice(8, 12),
      hex.slice(12, 16),
      hex.slice(16, 20),
      hex.slice(20),
    ].join("-");
  } catch {
    return null;
  }
}
