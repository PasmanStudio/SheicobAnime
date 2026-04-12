import { NextResponse } from "next/server";

export async function GET() {
  const apiUrl = process.env.NEXT_PUBLIC_API_URL ?? "(not set)";
  
  let apiReachable = false;
  let apiError = "";
  let seriesCount = 0;
  
  try {
    const res = await fetch(`${apiUrl}/series?pageSize=1`, {
      cache: "no-store",
      headers: { "Content-Type": "application/json" },
    });
    apiReachable = res.ok;
    if (res.ok) {
      const data = await res.json();
      seriesCount = data.total ?? 0;
    } else {
      apiError = `HTTP ${res.status}: ${await res.text().catch(() => "")}`;
    }
  } catch (err) {
    apiError = err instanceof Error ? err.message : String(err);
  }

  return NextResponse.json({
    NEXT_PUBLIC_API_URL: apiUrl,
    apiReachable,
    seriesCount,
    apiError: apiError || undefined,
    timestamp: new Date().toISOString(),
  });
}
