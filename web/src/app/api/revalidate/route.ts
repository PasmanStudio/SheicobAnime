import { revalidateTag } from "next/cache";
import { NextResponse, type NextRequest } from "next/server";

// Nunca cachear este endpoint.
export const dynamic = "force-dynamic";

/**
 * POST /api/revalidate — revalidación ON-DEMAND del contenido.
 *
 * Lo llama el scraper al terminar un run (ver MultiHostUploadService / pipeline):
 * purga el tag "content" (ver lib/api.ts CONTENT_CACHE) para que series/episodios/
 * mirrors nuevos aparezcan al instante, sin esperar el TTL de 60s.
 *
 * Auth: header `x-revalidate-secret` == env REVALIDATE_SECRET.
 *  - Si REVALIDATE_SECRET no está seteado → 503 (deshabilitado, no-op seguro).
 *  - Requiere el tag cache KV (NEXT_TAG_CACHE_KV) para surtir efecto; sin él
 *    revalidateTag no rompe nada, solo no purga (queda el TTL de 60s).
 *
 * Body opcional: { "tag": "content" } para purgar un tag distinto.
 */
export async function POST(req: NextRequest) {
  const secret = process.env.REVALIDATE_SECRET;
  if (!secret) {
    return NextResponse.json(
      { revalidated: false, reason: "REVALIDATE_SECRET no configurado" },
      { status: 503 },
    );
  }

  const provided = req.headers.get("x-revalidate-secret");
  if (provided !== secret) {
    return NextResponse.json({ revalidated: false, reason: "unauthorized" }, { status: 401 });
  }

  let tag = "content";
  try {
    const body = (await req.json()) as { tag?: unknown };
    if (typeof body?.tag === "string" && body.tag.trim()) tag = body.tag.trim();
  } catch {
    // sin body / body no-JSON → usa el tag por defecto "content"
  }

  revalidateTag(tag);
  return NextResponse.json({ revalidated: true, tag, now: Date.now() });
}
