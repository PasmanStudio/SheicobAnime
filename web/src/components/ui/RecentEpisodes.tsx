import EpisodeCard from "@/components/ui/EpisodeCard";
import type { Episode } from "@/lib/types";

interface RecentEpisodesProps {
  episodes: Episode[];
}

/** Group episodes by calendar date and render a schedule grid. */
export default function RecentEpisodes({ episodes }: RecentEpisodesProps) {
  const grouped = groupByDate(episodes);

  if (grouped.length === 0) {
    return (
      <div className="text-sm text-ink-3">
        <p>Todavía no hay episodios recientes.</p>
        <p className="mt-1">Volvé en un rato — el catálogo se actualiza todos los días.</p>
      </div>
    );
  }

  const dayMs = 24 * 60 * 60 * 1000;

  return (
    <div className="space-y-8">
      {grouped.map(({ label, items }) => (
        <div key={label}>
          <h3 className="sh-label mb-3 block">{label}</h3>
          <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6 gap-3.5">
            {items.map((ep) => {
              const series = ep.series;
              const href = series
                ? `/series/${series.slug}/${ep.episodeNumber}`
                : `/episodes/${ep.id}`;
              const created = new Date(ep.createdAt).getTime();
              return (
                <EpisodeCard
                  key={ep.id}
                  href={href}
                  seriesTitle={series?.title ?? ep.title ?? `Episodio ${ep.episodeNumber}`}
                  episodeNumber={ep.episodeNumber}
                  title={ep.title}
                  thumbnailUrl={ep.thumbnailUrl ?? series?.coverUrl ?? null}
                  timeAgo={formatTimeAgo(ep.createdAt)}
                  isNew={Date.now() - created < dayMs}
                />
              );
            })}
          </div>
        </div>
      ))}
    </div>
  );
}

// ─── Helpers ────────────────────────────────────────

interface DateGroup {
  label: string;
  items: Episode[];
}

// El sitio corre en Cloudflare Workers (UTC). Los límites de día (Hoy/Ayer) se
// calculan en hora argentina, no UTC — si no, un episodio de las 21:00 ART
// (= medianoche UTC) cae en "Ayer".
const SITE_TZ = "America/Argentina/Buenos_Aires";

/** Devuelve YYYY-MM-DD del instante dado, leído en hora argentina. */
function dateKey(d: Date): string {
  // en-CA da formato YYYY-MM-DD directamente
  return new Intl.DateTimeFormat("en-CA", {
    timeZone: SITE_TZ,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).format(d);
}

function groupByDate(episodes: Episode[]): DateGroup[] {
  const now = new Date();
  const todayKey = dateKey(now);
  const yesterdayKey = dateKey(new Date(now.getTime() - 86_400_000));

  const map = new Map<string, Episode[]>();

  for (const ep of episodes) {
    const key = dateKey(new Date(ep.createdAt));
    const existing = map.get(key);
    if (existing) {
      existing.push(ep);
    } else {
      map.set(key, [ep]);
    }
  }

  const groups: DateGroup[] = [];
  for (const [key, items] of Array.from(map.entries())) {
    let label: string;
    if (key === todayKey) {
      label = "Hoy";
    } else if (key === yesterdayKey) {
      label = "Ayer";
    } else {
      label = formatFullDate(key);
    }
    groups.push({ label, items });
  }

  return groups;
}

const DAY_NAMES = ["Domingo", "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado"];
const MONTH_NAMES = [
  "Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio",
  "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre",
];

function formatFullDate(key: string): string {
  // key ya viene en hora ART; lo interpretamos como fecha local "pura"
  const [year, month, day] = key.split("-").map(Number);
  const d = new Date(year, month - 1, day);
  return `${DAY_NAMES[d.getDay()]}, ${day} de ${MONTH_NAMES[d.getMonth()]}`;
}

function formatTimeAgo(iso: string): string {
  const diffMs = Date.now() - new Date(iso).getTime();
  const mins = Math.floor(diffMs / 60_000);
  if (mins < 60) return `hace ${Math.max(1, mins)} min`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `hace ${hours} h`;
  const days = Math.floor(hours / 24);
  if (days === 1) return "ayer";
  return `hace ${days} días`;
}
