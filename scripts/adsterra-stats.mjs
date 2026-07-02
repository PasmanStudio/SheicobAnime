// Adsterra Publishers API — reporte de ganancias por consola.
// Uso: node scripts/adsterra-stats.mjs [días] [placement|date|domain]
//   node scripts/adsterra-stats.mjs           → últimos 30 días por fecha
//   node scripts/adsterra-stats.mjs 7         → últimos 7 días por fecha
//   node scripts/adsterra-stats.mjs 30 placement → por placement (qué formato rinde)
// Token: ADSTERRA_API_TOKEN en env o en web/.env.local (nunca commitear).
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const API_BASE = "https://api3.adsterratools.com/publisher";

function loadToken() {
  if (process.env.ADSTERRA_API_TOKEN) return process.env.ADSTERRA_API_TOKEN;
  const envPath = join(dirname(fileURLToPath(import.meta.url)), "..", "web", ".env.local");
  try {
    const match = readFileSync(envPath, "utf8").match(/^ADSTERRA_API_TOKEN="?([^"\r\n]+)"?/m);
    if (match) return match[1];
  } catch {
    /* sin .env.local — cae al error de abajo */
  }
  console.error("Falta ADSTERRA_API_TOKEN (env o web/.env.local)");
  process.exit(1);
}

async function api(path, token) {
  const res = await fetch(`${API_BASE}/${path}`, { headers: { "X-API-Key": token } });
  if (!res.ok) throw new Error(`Adsterra API ${res.status}: ${await res.text()}`);
  return res.json();
}

const days = Number(process.argv[2] ?? 30);
const groupBy = process.argv[3] ?? "date";
const token = loadToken();

const fmt = (d) => d.toISOString().slice(0, 10);
const finish = new Date();
const start = new Date(finish.getTime() - days * 86_400_000);

const [domains, stats] = await Promise.all([
  api("domains.json", token),
  api(`stats.json?start_date=${fmt(start)}&finish_date=${fmt(finish)}&group_by=${groupBy}`, token),
]);

console.log(`Dominios registrados: ${domains.items.map((d) => d.title).join(", ")}`);
console.log(`Stats ${fmt(start)} → ${fmt(finish)} (agrupado por ${groupBy})\n`);

if (!stats.items.length) {
  console.log("Sin datos en el rango.");
  process.exit(0);
}

console.table(
  stats.items.map((r) => ({
    [groupBy]: r[groupBy] ?? r.date ?? r.placement ?? r.domain,
    impresiones: r.impression,
    clicks: r.clicks,
    ctr: r.ctr,
    cpm: r.cpm,
    "revenue $": r.revenue,
  })),
);

const total = stats.items.reduce(
  (a, r) => ({ imp: a.imp + r.impression, rev: a.rev + r.revenue }),
  { imp: 0, rev: 0 },
);
console.log(`TOTAL: ${total.imp} impresiones — $${total.rev.toFixed(3)}`);
