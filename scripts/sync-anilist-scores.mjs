#!/usr/bin/env node
/**
 * scripts/sync-anilist-scores.mjs
 *
 * Fetches every anime from AniList (paginated by year to avoid the
 * 5000-result API limit), matches against our series DB, and generates
 * a SQL UPDATE script with scores (0–10 scale).
 *
 * Usage (from project root):
 *   node scripts/sync-anilist-scores.mjs
 *
 * Optional env vars:
 *   API_URL   — defaults to production Render API
 *
 * Output:
 *   scripts/scores-update-YYYY-MM-DD.sql   ← run in Supabase SQL Editor
 *   scripts/unmatched-YYYY-MM-DD.txt       ← full list of unmatched series
 *
 * AniList rate limit: 90 req/min.
 * Fetching ~50 years × 5–10 pages each ≈ 10–15 min total.
 */

import { writeFileSync } from 'fs';

const API_BASE    = process.env.API_URL ?? 'https://sheicobanime-api.onrender.com';
const ANILIST_URL = 'https://graphql.anilist.co';
const DATE_TAG    = new Date().toISOString().slice(0, 10);
const OUTPUT      = `scripts/scores-update-${DATE_TAG}.sql`;
const UNMATCHED   = `scripts/unmatched-${DATE_TAG}.txt`;
const START_YEAR  = 1960;
const END_YEAR    = new Date().getFullYear() + 1;

// ─── Helpers ──────────────────────────────────────────────────────────────────

const sleep = ms => new Promise(r => setTimeout(r, ms));

/** Same normalization as web/src/lib/anilist.ts */
function normalize(t) {
  return t.toLowerCase().replace(/[^\p{L}\p{N}]/gu, '');
}

function titlesMatch(anilist, ourTitle, ourRomaji, ourNative) {
  const candidates = [
    anilist.title?.romaji,
    anilist.title?.english,
    anilist.title?.native,
  ].filter(Boolean).map(normalize).filter(t => t.length >= 2);

  const ours = [ourTitle, ourRomaji, ourNative]
    .filter(Boolean).map(normalize).filter(t => t.length >= 2);

  if (!candidates.length || !ours.length) return false;

  for (const c of candidates) {
    for (const o of ours) {
      if (c === o) return true;
      if (c.length >= 4 && o.length >= 4 && (c.includes(o) || o.includes(c))) return true;
    }
  }
  return false;
}

function escapeSql(str) { return str.replace(/'/g, "''"); }

// ─── AniList fetching (by year) ───────────────────────────────────────────────

const ANILIST_QUERY = `
query ($page: Int, $year: Int) {
  Page(page: $page, perPage: 50) {
    pageInfo { hasNextPage }
    media(type: ANIME, seasonYear: $year, sort: [ID]) {
      id
      title { romaji english native }
      averageScore
      meanScore
    }
  }
}`;

let totalRequests = 0;

async function fetchAniListPage(page, year) {
  while (true) {
    try {
      const resp = await fetch(ANILIST_URL, {
        method:  'POST',
        headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
        body:    JSON.stringify({ query: ANILIST_QUERY, variables: { page, year } }),
      });

      totalRequests++;

      if (resp.status === 429) {
        process.stdout.write(' [rate-limited, pausing 65s...]');
        await sleep(65_000);
        continue; // retry same page
      }

      if (!resp.ok) {
        const body = await resp.text();
        throw new Error(`AniList HTTP ${resp.status} (year=${year} page=${page}): ${body.slice(0, 200)}`);
      }

      const json = await resp.json();
      if (json.errors) throw new Error(`AniList GQL error: ${JSON.stringify(json.errors)}`);
      return json.data.Page;

    } catch (err) {
      if (err.message.includes('rate-limited')) throw err;
      // Network error — retry once after 5s
      process.stdout.write(` [network error, retrying: ${err.message.slice(0, 60)}]`);
      await sleep(5_000);
    }
  }
}

async function fetchAllAniList() {
  // Use a Map keyed by AniList ID to deduplicate (some anime span multiple years)
  const byId = new Map();
  const yearsTotal = END_YEAR - START_YEAR;
  let yearsProcessed = 0;

  for (let year = START_YEAR; year < END_YEAR; year++) {
    let page = 1;
    let hasNextPage = true;
    let yearCount = 0;

    while (hasNextPage) {
      const { media, pageInfo } = await fetchAniListPage(page, year);

      for (const m of media) {
        const score = m.averageScore ?? m.meanScore ?? 0;
        if (score > 0 && !byId.has(m.id)) {
          byId.set(m.id, m);
        }
      }

      yearCount += media.length;
      hasNextPage = pageInfo.hasNextPage;
      page++;

      // Stay under 90 req/min
      await sleep(700);
    }

    yearsProcessed++;
    const pct = ((yearsProcessed / yearsTotal) * 100).toFixed(0);
    process.stdout.write(
      `\r  [${pct}%] year ${year}: ${yearCount} anime | total with scores: ${byId.size} | requests: ${totalRequests}   `
    );
  }

  console.log(`\n  Done: ${byId.size} AniList entries with scores (${totalRequests} API requests)`);
  return [...byId.values()];
}

// ─── Our API fetching ─────────────────────────────────────────────────────────

async function fetchOurSeries() {
  const all = [];
  let page = 1;

  while (true) {
    let resp;
    // Render free tier cold-starts take up to 60s — retry on 502/503
    for (let attempt = 1; attempt <= 6; attempt++) {
      resp = await fetch(`${API_BASE}/series?page=${page}&pageSize=500`);
      if (resp.ok) break;
      if (resp.status === 502 || resp.status === 503) {
        process.stdout.write(`\r  API devolvió ${resp.status} (Render despertando...) intento ${attempt}/6, esperando ${attempt * 10}s...`);
        await sleep(attempt * 10_000);
      } else {
        throw new Error(`Our API HTTP ${resp.status} (page ${page})`);
      }
    }
    if (!resp.ok) throw new Error(`Our API HTTP ${resp.status} tras 6 intentos — verificá que la API esté corriendo`);

    const data = await resp.json();
    all.push(...data.data);
    process.stdout.write(`\r  Our API: ${all.length} series (página ${page})   `);
    if (data.data.length < 500) break;
    page++;
    await sleep(300);
  }

  console.log(`\n  Done: ${all.length} series en nuestra BD`);
  return all;
}

// ─── Main ─────────────────────────────────────────────────────────────────────

async function main() {
  console.log('╔════════════════════════════════════════╗');
  console.log('║   AniList → SheicobAnime score sync    ║');
  console.log('╚════════════════════════════════════════╝\n');

  console.log('① Fetching our series from API...');
  const ourSeries = await fetchOurSeries();

  console.log(`\n② Fetching AniList by year (${START_YEAR}→${END_YEAR - 1}) — ~10–15 min...`);
  const anilistAll = await fetchAllAniList();

  console.log('\n③ Matching...');
  const matched   = [];
  const unmatched = [];

  for (const s of ourSeries) {
    const hit = anilistAll.find(a =>
      titlesMatch(a, s.title, s.titleRomaji, s.titleNative)
    );
    if (hit) {
      const rawScore = hit.averageScore ?? hit.meanScore;
      matched.push({
        slug:         s.slug,
        ourTitle:     s.title,
        anilistTitle: hit.title.romaji ?? hit.title.english ?? hit.title.native,
        score:        (rawScore / 10).toFixed(1),
      });
    } else {
      unmatched.push(s);
    }
  }

  // ── SQL output ──
  const lines = [
    `-- AniList → SheicobAnime score sync`,
    `-- Generated: ${new Date().toISOString()}`,
    `-- Matched:   ${matched.length} / ${ourSeries.length} series`,
    `-- Unmatched: ${unmatched.length} series`,
    `--`,
    `-- Run in Supabase: Dashboard → SQL Editor → paste → Run`,
    ``,
    ...matched.map(m =>
      `UPDATE series SET score = ${m.score} WHERE slug = '${escapeSql(m.slug)}'; -- ${m.anilistTitle}`
    ),
  ];
  writeFileSync(OUTPUT, lines.join('\n') + '\n');

  // ── Unmatched output ──
  writeFileSync(
    UNMATCHED,
    [
      `# Series sin match en AniList — ${DATE_TAG}`,
      `# Columnas: slug | title | titleRomaji`,
      ``,
      ...unmatched.map(s =>
        `${s.slug}\t${s.title}\t${s.titleRomaji ?? ''}`
      ),
    ].join('\n') + '\n'
  );

  // ── Console report ──
  const matchRate = ((matched.length / ourSeries.length) * 100).toFixed(1);
  const scores    = matched.map(m => parseFloat(m.score));
  const minScore  = Math.min(...scores).toFixed(1);
  const maxScore  = Math.max(...scores).toFixed(1);
  const avgScore  = (scores.reduce((a, b) => a + b, 0) / scores.length).toFixed(2);

  const dist = { '9–10': 0, '8–9': 0, '7–8': 0, '6–7': 0, '5–6': 0, '<5': 0 };
  for (const sc of scores) {
    if      (sc >= 9) dist['9–10']++;
    else if (sc >= 8) dist['8–9']++;
    else if (sc >= 7) dist['7–8']++;
    else if (sc >= 6) dist['6–7']++;
    else if (sc >= 5) dist['5–6']++;
    else              dist['<5']++;
  }

  console.log('\n╔════════════════════════════════════════╗');
  console.log('║              REPORTE                   ║');
  console.log('╚════════════════════════════════════════╝');
  console.log(`  Series en nuestra BD:   ${ourSeries.length}`);
  console.log(`  Matcheadas con score:   ${matched.length}  (${matchRate}%)`);
  console.log(`  Sin match:              ${unmatched.length}`);
  console.log(`  Score promedio:         ${avgScore} / 10`);
  console.log(`  Rango:                  ${minScore} – ${maxScore}`);
  console.log('\n  Distribución de scores:');
  for (const [range, count] of Object.entries(dist)) {
    const bar = '█'.repeat(Math.round(count / matched.length * 40));
    console.log(`    ${range.padEnd(6)}: ${String(count).padStart(4)}  ${bar}`);
  }

  // ── Why unmatched? Analyze patterns ──
  const cjkOnly    = unmatched.filter(s => !s.titleRomaji && /^[　-鿿＀-￯]+$/.test(s.title));
  const noRomaji   = unmatched.filter(s => !s.titleRomaji && !cjkOnly.includes(s));
  const hasRomaji  = unmatched.filter(s => s.titleRomaji);

  console.log('\n  Por qué no matchearon:');
  console.log(`    Con romaji pero sin match:   ${hasRomaji.length}  ← nombres muy distintos o no están en AniList`);
  console.log(`    Sin romaji (título latino):  ${noRomaji.length}  ← puede funcionar por título`);
  console.log(`    Solo CJK sin romaji:         ${cjkOnly.length}   ← difícil de matchear automáticamente`);

  console.log(`\n  Primeros 30 sin match:`);
  unmatched.slice(0, 30).forEach(s => {
    const romaji = s.titleRomaji ? ` [${s.titleRomaji}]` : '';
    console.log(`    - ${s.title}${romaji}`);
  });
  if (unmatched.length > 30) console.log(`    ... y ${unmatched.length - 30} más (ver ${UNMATCHED})`);

  console.log(`\n✅ SQL guardado en:       ${OUTPUT}`);
  console.log(`📄 Sin match guardado en: ${UNMATCHED}`);
  console.log(`\n👉 Siguiente paso: abrir Supabase → SQL Editor → pegar ${OUTPUT} → Run`);
}

main().catch(err => {
  console.error('\n❌ Error:', err.message);
  process.exit(1);
});
