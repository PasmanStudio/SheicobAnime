import type { Episode } from "@/lib/types";
import Image from "next/image";
import Link from "next/link";

interface RecentEpisodesProps {
  episodes: Episode[];
}

/** Group episodes by calendar date (Buenos Aires TZ) and render a JKAnime‑style schedule grid. */
export default function RecentEpisodes({ episodes }: RecentEpisodesProps) {
  const grouped = groupByDate(episodes);

  if (grouped.length === 0) {
    return (
      <p className="text-zinc-500 text-sm">No hay episodios recientes.</p>
    );
  }

  return (
    <div className="space-y-8">
      {grouped.map(({ label, items }) => (
        <div key={label}>
          <h3 className="text-sm font-semibold text-indigo-400 uppercase tracking-wider mb-3">
            {label}
          </h3>
          <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6 gap-3">
            {items.map((ep) => (
              <EpisodeCard key={ep.id} episode={ep} />
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

function EpisodeCard({ episode }: { episode: Episode }) {
  const series = episode.series;
  const href = series
    ? `/series/${series.slug}/${episode.episodeNumber}`
    : `/episodes/${episode.id}`;
  const coverUrl = episode.thumbnailUrl ?? series?.coverUrl ?? null;

  return (
    <Link
      href={href}
      className="group block rounded-lg overflow-hidden bg-neutral-800 hover:ring-2 hover:ring-indigo-500 transition-all"
    >
      {/* Thumbnail — 16:9 */}
      <div className="relative aspect-video bg-neutral-700 overflow-hidden">
        {coverUrl ? (
          <Image
            src={coverUrl}
            alt={series?.title ?? `Episodio ${episode.episodeNumber}`}
            fill
            sizes="(max-width: 640px) 50vw, (max-width: 1024px) 25vw, 16vw"
            className="object-cover group-hover:scale-105 transition-transform duration-300"
          />
        ) : (
          <div className="w-full h-full flex items-center justify-center text-neutral-500 text-xs">
            Sin imagen
          </div>
        )}

        {/* Episode number badge */}
        <span className="absolute top-1.5 left-1.5 bg-indigo-600 text-white text-[10px] font-bold px-1.5 py-0.5 rounded">
          Ep {episode.episodeNumber}
        </span>

        {/* Air day badge */}
        {episode.airedAt && (
          <span className="absolute top-1.5 right-1.5 bg-black/70 text-zinc-300 text-[10px] px-1.5 py-0.5 rounded">
            {formatShortDay(episode.airedAt)}
          </span>
        )}
      </div>

      {/* Title */}
      <div className="p-2">
        <p className="text-xs text-white font-medium line-clamp-2 leading-tight">
          {series?.title ?? episode.title ?? `Episodio ${episode.episodeNumber}`}
        </p>
      </div>
    </Link>
  );
}

// ─── Helpers ────────────────────────────────────────

interface DateGroup {
  label: string;
  items: Episode[];
}

function groupByDate(episodes: Episode[]): DateGroup[] {
  const now = new Date();
  const todayKey = dateKey(now);
  const yesterdayKey = dateKey(addDays(now, -1));

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

function dateKey(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
}

function addDays(d: Date, n: number): Date {
  const result = new Date(d);
  result.setDate(result.getDate() + n);
  return result;
}

const DAY_NAMES = ["Domingo", "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado"];
const MONTH_NAMES = [
  "Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio",
  "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre",
];

function formatFullDate(key: string): string {
  const [year, month, day] = key.split("-").map(Number);
  const d = new Date(year, month - 1, day);
  return `${DAY_NAMES[d.getDay()]}, ${day} de ${MONTH_NAMES[d.getMonth()]}`;
}

function formatShortDay(iso: string): string {
  const d = new Date(iso);
  return `${DAY_NAMES[d.getDay()].slice(0, 3)} ${d.getDate()}`;
}
