import AdSlot from "@/components/ads/AdSlot";
import InactivityAdTrigger from "@/components/ads/InactivityAdTrigger";
import ContinueWatchingRow from "@/components/ui/ContinueWatchingRow";
import GenreChip from "@/components/ui/GenreChip";
import SeasonPollBanner from "@/components/polls/SeasonPollBanner";
import HeroCarousel from "@/components/ui/HeroCarousel";
import PendingSeriesRow from "@/components/ui/PendingSeriesRow";
import RankNumber from "@/components/ui/RankNumber";
import RecentEpisodes from "@/components/ui/RecentEpisodes";
import SectionHeader from "@/components/ui/SectionHeader";
import SeriesCard from "@/components/ui/SeriesCard";
import { getGenres, getRecentEpisodes, getSeries } from "@/lib/api";
import type { Episode, Genre, PaginatedResponse, Series } from "@/lib/types";
import type { Metadata } from "next";

// ISR: el HTML del home se cachea en el edge (Cloudflare KV). La frescura real la
// da la PURGA on-demand (revalidateTag("content") desde el scraper invalida también
// el cache de página, no solo los fetches), así que el episodio nuevo aparece al
// instante. Este `revalidate` es solo el respaldo de fondo: 3600s (1h) para no
// quemar el free tier de KV (1000 writes/día) — cada regeneración por TTL es un
// write, y el home es de las rutas más golpeadas. Las secciones por-usuario son
// client components y se mantienen frescas.
export const revalidate = 3600;

export const metadata: Metadata = {
  title: "SheicobAnime — Mira anime online en español",
  description:
    "Mira los últimos episodios de anime online, sub español. Catálogo actualizado todos los días.",
};

const EMPTY_PAGE: PaginatedResponse<Series> = { data: [], total: 0, page: 1, pageSize: 12 };

export default async function HomePage() {
  // Cada fetch falla de forma independiente: si Render está frío y uno se cae,
  // las demás secciones siguen renderizando (antes un solo Promise.all que
  // rechazaba dejaba TODA la home vacía).
  const [recent, topRated, recentEpisodes, genres] = await Promise.all([
    getSeries({ sort: "updated", pageSize: 12 }).catch(() => EMPTY_PAGE),
    getSeries({ sort: "score", pageSize: 10 }).catch(() => EMPTY_PAGE),
    getRecentEpisodes({ days: 3, pageSize: 30 }).catch(() => [] as Episode[]),
    getGenres().catch(() => [] as Genre[]),
  ]);

  return (
    <div className="mx-auto max-w-container px-4 py-6 space-y-10">
      <InactivityAdTrigger />

      {/* Hero */}
      {recent.data.length > 0 && <HeroCarousel series={recent.data} />}

      {/* Banner de encuesta de temporada (se oculta solo si no hay activa) */}
      <SeasonPollBanner />

      {/* Continuar viendo (episodios a medio ver) — primero, client-side */}
      <ContinueWatchingRow />

      {/* Seguir mirando (series con el siguiente episodio pendiente) */}
      <PendingSeriesRow />

      {/* Últimos episodios */}
      <section>
        <SectionHeader
          title="Últimos episodios"
          eyebrow="Actualizado todos los días"
          className="mb-4"
        />
        <RecentEpisodes episodes={recentEpisodes} />
      </section>

      <AdSlot placement="home_mid" />

      {/* Top 10 */}
      <section>
        <SectionHeader
          title="Top 10 de la comunidad"
          action="Ranking completo"
          actionHref="/ranking"
          className="mb-4"
        />
        <div className="sh-scroll-row items-end pb-1.5">
          {topRated.data.slice(0, 10).map((s, i) => (
            <div key={s.id} className="flex items-end shrink-0">
              <RankNumber
                rank={i + 1}
                size="lg"
                className="z-10 -mr-3.5 mb-2"
                style={{ textShadow: "0 2px 8px rgba(0,0,0,0.8)" }}
              />
              <SeriesCard series={s} className="w-[150px]" />
            </div>
          ))}
        </div>
      </section>

      {/* Explorar géneros */}
      {genres.length > 0 && (
        <section>
          <SectionHeader title="Explorá por género" className="mb-4" />
          <div className="flex flex-wrap gap-2">
            {genres.slice(0, 16).map((g) => (
              <GenreChip key={g.id} name={g.name} />
            ))}
          </div>
        </section>
      )}

      <AdSlot placement="home_bottom" />
    </div>
  );
}
