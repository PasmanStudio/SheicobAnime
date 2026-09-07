import AdSlot from "@/components/ads/AdSlot";
import SeasonCard from "@/components/ui/SeasonCard";
import SeriesCard from "@/components/ui/SeriesCard";
import {
  getCurrentSeason,
  getSeasonalAnime,
  getSeasonNav,
  SEASON_LABELS,
  SEASON_ORDER,
  SeasonUnavailableError,
  titlesMatch,
  type AniListSeason,
} from "@/lib/anilist";
import { getSeries, getSeriesFast, searchSeriesFast } from "@/lib/api";
import type { Series } from "@/lib/types";
import type { Metadata } from "next";
import Link from "next/link";

// force-dynamic: la página lee `searchParams` (season/year), así que Next la
// renderiza dinámica igual. Lo que importa para la resiliencia no es el modo de
// la página sino que los DATOS estén cacheados: `getSeasonalAnime` cachea 6 h y
// `getSeries` usa CONTENT_CACHE. Antes AniList iba con `cache: "no-store"` y
// cada visita pegaba en vivo a un Render dormido → temporada vacía.
export const dynamic = "force-dynamic";

// Tope del fan-out de búsquedas por título (fase 3).
//
// Cloudflare Workers en plan free permite 50 SUBREQUESTS por request. La fase 3
// hacía `unmatched.map(searchSeries)` sin tope: una temporada de AniList trae
// 100-300 títulos, así que con más de ~45 sin matchear el Worker se pasaba del
// límite y reventaba el render entero — otra causa, independiente de Render, de
// que /temporada saliera vacía. Se priorizan los más populares, que son los que
// el usuario espera ver marcados como disponibles.
//
// PRESUPUESTO DE SUBREQUESTS DE ESTA PÁGINA (límite: 50 en el plan free).
// Peor caso, con los reintentos de lib/api.ts contados:
//     getSeasonalAnime .....  3  (hasta 3 intentos)
//     getSeries ongoing ....  3
//     getSeries por score ..  3
//     searchSeriesFast ..... 30  (1 intento cada una, por eso es "Fast")
//                            ──
//                            39  → quedan 11 de margen
// Si agregás un fetch nuevo a esta página, restalo de esos 11 o bajá este tope.
// Pasarse del límite no degrada: tira el render entero.
const MAX_TITLE_SEARCHES = 30;

interface Props {
  searchParams: Promise<{ season?: string; year?: string }>;
}

export async function generateMetadata({ searchParams }: Props): Promise<Metadata> {
  const sp = await searchParams;
  const { season, year } = resolveParams(sp);
  const seasonLabel = SEASON_LABELS[season];
  return {
    title: `Temporada ${seasonLabel} ${year} — SheicobAnime`,
    description: `Anime de la temporada ${seasonLabel.toLowerCase()} ${year}. Descubre qué títulos están disponibles en SheicobAnime.`,
  };
}

function resolveParams(sp: { season?: string; year?: string }): {
  season: AniListSeason;
  year: number;
} {
  const { season: defaultSeason, year: defaultYear } = getCurrentSeason();
  const season =
    sp.season && (SEASON_ORDER as string[]).includes(sp.season)
      ? (sp.season as AniListSeason)
      : defaultSeason;
  const year = sp.year ? parseInt(sp.year, 10) || defaultYear : defaultYear;
  return { season, year };
}

export default async function TemporadaPage({ searchParams }: Props) {
  const sp = await searchParams;
  const { season, year } = resolveParams(sp);
  const { season: currentSeason, year: currentYear } = getCurrentSeason();

  // ── Phase 1: broad pre-fetch in parallel ─────────────────────────────────────
  //
  // Two catalogue fetches to cover the most common cases:
  //   1. status=ongoing — all currently airing series (~87). New seasonal entries
  //      have score=null so they're invisible in score-sorted queries.
  //   2. sort=score&pageSize=500 — top 500 by score. Covers popular completed
  //      series when browsing past seasons.
  //
  const fallback = { data: [] as Series[], total: 0, page: 1, pageSize: 500 };
  // `seasonUnavailable` separa "no pudimos consultar" de "la temporada no tiene
  // estrenos todavía". Sin esa distinción los dos casos mostraban el mismo
  // cartel y una caída del API se leía como una temporada vacía.
  let seasonUnavailable = false;
  const [anilistData, ongoingResult, topByScoreResult] = await Promise.all([
    getSeasonalAnime(season, year).catch((err) => {
      if (err instanceof SeasonUnavailableError) {
        seasonUnavailable = true;
        return [];
      }
      throw err;
    }),
    getSeries({ pageSize: 500, status: "ongoing" }).catch(() => fallback),
    getSeries({ pageSize: 500, sort: "score" }).catch(() => fallback),
  ]);

  // ── Fallback: nuestro propio catálogo cuando AniList no está ────────────────
  //
  // AniList apagó su API pública (403 "temporarily disabled due to severe
  // stability issues", verificado el 7-sep-2026 desde Render y desde una IP
  // residencial). Sin esto la página quedaría mostrando un cartel de error de
  // forma indefinida, porque toda su grilla salía de AniList.
  //
  // Pero los datos ya los tenemos: `Series.season` viene poblado en 94 de 95
  // series en emisión — 69 de ellas marcadas "Verano 2026". Así que cuando el
  // upstream se cae, la grilla se arma con lo NUESTRO. Es una página distinta y
  // en algún sentido mejor: en vez del top-50 de popularidad de AniList (del que
  // el usuario solo puede ver lo que tengamos indexado), muestra exactamente los
  // títulos de la temporada que sí se pueden mirar acá. Se avisa el cambio para
  // no fingir que es la misma información.
  //
  // `Series.season` es texto en español ("Verano 2026"), el mismo label que
  // SEASON_LABELS, así que el match es directo.
  //
  // Se dispara tanto si el API avisó el fallo (503 → seasonUnavailable) como si
  // devolvió una lista vacía. Hoy hacen falta las dos: el endpoint del API
  // todavía enmascara el 403 de AniList como `200 []` (el fix solo llega al
  // mergear, porque Render auto-deploya desde main), y aunque eso se corrija,
  // una lista vacía con títulos nuestros para esa temporada significa lo mismo
  // para el usuario: hay algo para mostrar y no lo estábamos mostrando.
  const anilistUnusable = seasonUnavailable || anilistData.length === 0;

  // getSeriesFast (un intento, 6 s): esta es la SEGUNDA fase de la página, ya
  // gastamos presupuesto arriba. Sumar dos presupuestos completos de reintentos
  // es lo que llevó el wall time del Worker a 40 s y lo hizo exceder sus
  // límites (error 1102) el 7-sep-2026.
  const ownCatalogue: Series[] = anilistUnusable
    ? await getSeriesFast({ year, pageSize: 500 })
        .then((r) =>
          r.data.filter((serie) =>
            (serie.season ?? "").toLowerCase().startsWith(SEASON_LABELS[season].toLowerCase()),
          ),
        )
        .catch(() => [] as Series[])
    : [];

  // Merge and deduplicate by slug (ongoing takes priority)
  const seenSlugs = new Set<string>();
  const prefetched = [...ongoingResult.data, ...topByScoreResult.data].filter((s) => {
    if (seenSlugs.has(s.slug)) return false;
    seenSlugs.add(s.slug);
    return true;
  });

  // ── Phase 2: first-pass title matching ────────────────────────────────────────
  const firstPass = anilistData.map((media) => ({
    media,
    match: prefetched.find((s) => titlesMatch(media, s.title, s.titleRomaji, s.titleNative)) ?? null,
  }));

  // ── Phase 3: search fallback for unmatched entries ────────────────────────────
  //
  // Many indexed series are status=completed with score=null (e.g. recently
  // finished shows), so they don't appear in either pre-fetch. For each AniList
  // entry still unmatched, we run a targeted search against our DB. This runs
  // server-to-server (low latency) and all searches fire in parallel.
  //
  const allUnmatched = firstPass
    .filter((m) => m.match === null)
    .sort((a, b) => (b.media.popularity ?? 0) - (a.media.popularity ?? 0));
  const unmatched = allUnmatched.slice(0, MAX_TITLE_SEARCHES);
  // Cuando se trunca, el conteo de disponibles es una COTA INFERIOR: puede
  // haber series indexadas entre las que no llegamos a buscar, y se muestran
  // como "No indexado aún". Se marca con un "+" en vez de dar un número exacto
  // que sabemos que puede estar bajo.
  const searchTruncated = allUnmatched.length > unmatched.length;
  const fallbackMap = new Map<number, Series>(); // AniList media.id → matched Series

  if (unmatched.length > 0) {
    const searches = await Promise.all(
      unmatched.map(async ({ media }) => {
        const query = media.title.english ?? media.title.romaji;
        if (!query) return null;
        const results = await searchSeriesFast({ q: query, pageSize: 5 }).catch(
          () => ({ data: [] as Series[] }),
        );
        const found = results.data.find((s) =>
          titlesMatch(media, s.title, s.titleRomaji, s.titleNative),
        );
        return found ? { id: media.id, series: found } : null;
      }),
    );

    for (const hit of searches) {
      if (hit) fallbackMap.set(hit.id, hit.series);
    }
  }

  // ── Final: combine both passes ────────────────────────────────────────────────
  const matched = firstPass.map((m) =>
    m.match !== null
      ? m
      : { media: m.media, match: fallbackMap.get(m.media.id) ?? null },
  );

  const availableCount = matched.filter((m) => m.match !== null).length;
  const seasonNav = getSeasonNav(year);

  // Year navigation (previous/next)
  const prevYear = year - 1;
  const nextYear = year + 1;
  const isCurrentYear = year === currentYear;

  return (
    <div className="mx-auto max-w-container px-4 py-8 space-y-6">
      {/* Header */}
      <div className="flex flex-col sm:flex-row sm:items-end justify-between gap-3">
        <div className="flex flex-col gap-1">
          <span className="sh-label">
            {anilistUnusable && ownCatalogue.length > 0
              ? `${ownCatalogue.length} ${ownCatalogue.length === 1 ? "título" : "títulos"} en SheicobAnime`
              : seasonUnavailable
                ? "Temporada no disponible"
                : `${availableCount}${searchTruncated ? "+" : ""} de ${anilistData.length} títulos disponibles`}
          </span>
          <span className="sh-section-header items-center">
            <span className="sh-cut" />
            <h1 className="sh-display text-[clamp(22px,3vw,28px)]">
              {SEASON_LABELS[season]} {year}
            </h1>
          </span>
        </div>

        {/* Year navigation */}
        <div className="flex items-center gap-2 text-sm">
          <Link
            href={`/temporada?season=${season}&year=${prevYear}`}
            className="sh-stat px-3 py-1.5 rounded-btn bg-abyss-2 border border-line-1 text-xs text-ink-3 hover:text-ink-1 hover:border-line-2 transition-colors duration-fast"
          >
            ← {prevYear}
          </Link>
          <span className="sh-stat px-3 py-1.5 rounded-btn bg-abyss-3 border border-line-2 text-xs text-ink-1">
            {year}
          </span>
          {!isCurrentYear && (
            <Link
              href={`/temporada?season=${season}&year=${nextYear}`}
              className="sh-stat px-3 py-1.5 rounded-btn bg-abyss-2 border border-line-1 text-xs text-ink-3 hover:text-ink-1 hover:border-line-2 transition-colors duration-fast"
            >
              {nextYear} →
            </Link>
          )}
        </div>
      </div>

      {/* Season tabs */}
      <div className="flex gap-2 overflow-x-auto pb-1">
        {seasonNav.map(({ season: s }) => {
          const isActive = s === season;
          const isCurrent = s === currentSeason && year === currentYear;
          return (
            <Link
              key={s}
              href={`/temporada?season=${s}&year=${year}`}
              className={`flex items-center gap-2 px-4 py-2 rounded-full text-sm font-semibold whitespace-nowrap border transition-colors duration-fast ${
                isActive
                  ? "bg-[var(--accent-muted)] text-brand-bright border-[var(--accent-border)]"
                  : "bg-abyss-2 text-ink-3 border-line-1 hover:text-ink-1 hover:border-line-2"
              }`}
            >
              {isActive && <span className="sh-cut !mr-0 !w-[3px] !h-3" />}
              {SEASON_LABELS[s]}
              {isCurrent && <span className="sh-live-dot !w-1.5 !h-1.5" />}
            </Link>
          );
        })}
      </div>

      {/* Grid */}
      {anilistUnusable && ownCatalogue.length > 0 ? (
        <>
          <p className="text-xs text-ink-3 border border-line-1 bg-abyss-2 rounded-btn px-3 py-2">
            La guía de estrenos externa no está disponible en este momento. Mientras tanto,
            estos son los títulos de la temporada que ya están en SheicobAnime.
          </p>
          <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 gap-4">
            {ownCatalogue.map((serie) => (
              <SeriesCard key={serie.slug} series={serie} />
            ))}
          </div>
        </>
      ) : seasonUnavailable ? (
        <div className="text-center py-20 text-sm">
          <p className="text-ink-2">No pudimos cargar la temporada en este momento.</p>
          <p className="mt-1 text-ink-3">
            Es un problema temporal nuestro, no de la temporada. Recargá en unos segundos.
          </p>
        </div>
      ) : anilistData.length === 0 ? (
        <div className="text-center py-20 text-sm">
          <p className="text-ink-2">Todavía no hay información de esta temporada.</p>
          <p className="mt-1 text-ink-3">Los estrenos se cargan apenas se anuncian — volvé en unos días.</p>
        </div>
      ) : (
        <>
          {/* Legend */}
          <div className="flex items-center gap-4 text-xs text-ink-3">
            <span className="flex items-center gap-1.5">
              <span className="inline-block w-2.5 h-2.5 rounded-sm bg-[var(--success)]" />
              Disponible en SheicobAnime
            </span>
            <span className="flex items-center gap-1.5">
              <span className="inline-block w-2.5 h-2.5 rounded-sm bg-abyss-3 border border-line-2" />
              No indexado aún
            </span>
          </div>

          <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 gap-4">
            {matched.map(({ media, match }) => (
              <SeasonCard key={media.id} media={media} match={match} />
            ))}
          </div>
        </>
      )}

      <AdSlot placement="directory_bottom" />
    </div>
  );
}
