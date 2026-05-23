#!/usr/bin/env node
/**
 * scripts/sync-anilist-scores.mjs
 *
 * Fetches every anime from AniList, matches against our series DB,
 * and generates a SQL UPDATE script with scores (0–10 scale).
 *
 * Usage (from project root):
 *   node scripts/sync-anilist-scores.mjs
 *
 * Optional env vars:
 *   API_URL   — defaults to production Render API
 *
 * Output:
 *   scripts/scores-update-YYYY-MM-DD.sql   ← run this in Supabase SQL Editor
 *   Console report with matched / unmatched breakdown
 *
 * AniList rate limit: 90 req/min → we send 1 req per 700 ms (safe margin).
 * Full fetch takes ~5–8 minutes.
 */

import { writeFileSync } from 'fs';

const API_BASE   = process.env.API_URL ?? 'https://sheicobanime-api.onrender.com';
const ANILIST_URL = 'https://graphql.anilist.co';
const DATE_TAG   = new Date().toISOString().slice(0, 10);
const OUTPUT     = `scripts/scores-update-${DATE_TAG}.sql`;

// ─── Helpers ──────────────────────────────────────────────────────────────────

const sleep = ms => new Promise(r => setTimeout(r, ms));

/** Same normalization used in web/src/lib/anilist.ts */
function normalize(t) {
  return t.toLowerCase().replace(/[^\p{L}\p{N}]/gu, '');
}

/**
 * Returns true if the AniList entry matches any of our title variants.
 * Exact match on normalized strings, or substring match for titles ≥ 4 chars.
 */
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

function escapeSql(str) {
  return str.replace(/'/g, "''");
}

// ─── AniList fetching ─────────────────────────────────────────────────────────

const ANILIST_QUERY = `
query ($page: Int) {
  Page(page: $page, perPage: 50) {
    pageInfo { hasNextPage currentPage total }
    media(type: ANIME) {
      id
      title { romaji english native }
      averageScore
      meanScore
    }
  }
}`;

async function fetchAniListPage(page) {
  const resp = await fetch(ANILIST_URL, {
    method:  'POST',
    headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
    body:    JSON.stringify({ query: ANILIST_QUERY, variables: { page } }),
  });

  if (resp.status === 429) {
    // Rate limited — back off 60 seconds
    process.stdout.write(' [rate-limited, waiting 60s]');
    await sleep(60_000);
    return fetchAniListPage(page);
  }

  if (!resp.ok) throw new Error(`AniList HTTP ${resp.status} on page ${page}`);

  const json = await resp.json();
  if (json.errors) throw new Error(`AniList error: ${JSON.stringify(json.errors)}`);
  return json.data.Page;
}

async function fetchAllAniList() {
  const all = [];
  let page = 1;
  let totalPages = '?';

  while (true) {
    const { media, pageInfo } = await fetchAniListPage(page);

    // Only keep entries that have at least one score
    const withScore = media.filter(m => (m.averageScore ?? m.meanScore ?? 0) > 0);
    all.push(...withScore);

    if (page === 1) {
      // Estimate total pages from total media count
      totalPages = Math.ceil(pageInfo.total / 50);
    }

    process.stdout.write(`\r  AniList: page ${page}/${totalPages} — ${all.length} with scores so far   `);

    if (!pageInfo.hasNextPage) break;
    page++;

    // Stay under 90 req/min (= 1 per ~667ms). Use 700ms to be safe.
    await sleep(700);
  }

  console.log(`\n  Done: ${all.length} AniList entries with scores (${page} pages)`);
  return all;
}

// ─── Our API fetching ─────────────────────────────────────────────────────────

async function fetchOurSeries() {
  const all = [];
  let page = 1;

  while (true) {
    const url = `${API_BASE}/series?page=${page}&pageSize=500`;
    const resp = await fetch(url);
    if (!resp.ok) throw new Error(`Our API HTTP ${resp.status} for page ${page}`);
    const data = await resp.json();

    all.push(...data.data);
    process.stdout.write(`\r  Our API: ${all.length} series fetched (page ${page})   `);

    if (data.data.length < 500) break;
    page++;
    await sleep(200);
  }

  console.log(`\n  Done: ${all.length} total series in our DB`);
  return all;
}

// ─── Main ─────────────────────────────────────────────────────────────────────

async function main() {
  console.log('╔════════════════════════════════════════╗');
  console.log('║   AniList → SheicobAnime score sync    ║');
  console.log('╚════════════════════════════════════════╝\n');

  // ── Step 1: fetch our series ──
  console.log('① Fetching series from our API...');
  const ourSeries = await fetchOurSeries();

  // ── Step 2: fetch AniList ──
  console.log('\n② Fetching AniList (≈5–8 min at safe rate limit)...');
  const anilistAll = await fetchAllAniList();

  // ── Step 3: match ──
  console.log('\n③ Matching...');

  const matched   = [];
  const unmatched = [];

  for (const s of ourSeries) {
    const hit = anilistAll.find(a =>
      titlesMatch(a, s.title, s.titleRomaji, s.titleNative)
    );

    if (hit) {
      const rawScore = hit.averageScore ?? hit.meanScore;
      const score    = (rawScore / 10).toFixed(1);  // 0–100 → 0.0–10.0
      matched.push({
        slug:         s.slug,
        ourTitle:     s.title,
        anilistTitle: hit.title.romaji ?? hit.title.english ?? hit.title.native,
        score,
      });
    } else {
      unmatched.push(s);
    }
  }

  // ── Step 4: generate SQL ──
  const header = [
    `-- AniList → SheicobAnime score sync`,
    `-- Generated: ${new Date().toISOString()}`,
    `-- Matched:   ${matched.length} series`,
    `-- Unmatched: ${unmatched.length} series`,
    `--`,
    `-- Run in Supabase SQL Editor (Dashboard → SQL Editor → paste → Run)`,
    ``,
  ].join('\n');

  const updates = matched.map(m =>
    `UPDATE series SET score = ${m.score} WHERE slug = '${escapeSql(m.slug)}'; -- ${m.anilistTitle}`
  ).join('\n');

  writeFileSync(OUTPUT, header + updates + '\n');
  console.log(`  SQL written to: ${OUTPUT}`);

  // ── Step 5: report ──
  const matchRate = ((matched.length / ourSeries.length) * 100).toFixed(1);

  console.log('\n╔════════════════════════════════════════╗');
  console.log('║              REPORT                    ║');
  console.log('╚════════════════════════════════════════╝');
  console.log(`  Total series in DB:  ${ourSeries.length}`);
  console.log(`  Matched with score:  ${matched.length}  (${matchRate}%)`);
  console.log(`  Unmatched:           ${unmatched.length}`);
  console.log(`\n  Score range: ${Math.min(...matched.map(m => parseFloat(m.score))).toFixed(1)} – ${Math.max(...matched.map(m => parseFloat(m.score))).toFixed(1)}`);

  // Score distribution
  const buckets = { '9-10': 0, '8-9': 0, '7-8': 0, '6-7': 0, '<6': 0 };
  for (const m of matched) {
    const sc = parseFloat(m.score);
    if (sc >= 9)      buckets['9-10']++;
    else if (sc >= 8) buckets['8-9']++;
    else if (sc >= 7) buckets['7-8']++;
    else if (sc >= 6) buckets['6-7']++;
    else              buckets['<6']++;
  }
  console.log('\n  Score distribution:');
  for (const [range, count] of Object.entries(buckets)) {
    console.log(`    ${range}: ${count} series`);
  }

  // Unmatched breakdown — try to guess why
  console.log(`\n  ─── Unmatched series (${unmatched.length} total) ───`);
  console.log('  (likely causes: title mismatch, CJK-only title, long-running series)');
  console.log('');

  const toShow = unmatched.slice(0, 60);
  for (const s of toShow) {
    const hasRomaji = s.titleRomaji ? ` [romaji: ${s.titleRomaji}]` : '';
    console.log(`  - ${s.slug}${hasRomaji}`);
  }
  if (unmatched.length > 60) {
    console.log(`  ... and ${unmatched.length - 60} more`);
    // Write full unmatched list to a separate file
    const unmatchedOutput = `scripts/unmatched-${DATE_TAG}.txt`;
    writeFileSync(
      unmatchedOutput,
      unmatched.map(s => `${s.slug}\t${s.title}\t${s.titleRomaji ?? ''}`).join('\n')
    );
    console.log(`  Full list saved to: ${unmatchedOutput}`);
  }

  console.log(`\n✅ Done. Next step: run ${OUTPUT} in Supabase SQL Editor.`);
}

main().catch(err => {
  console.error('\n❌ Error:', err.message);
  process.exit(1);
});
