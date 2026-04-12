import type { Series, SeriesStatus, SeriesType } from "@/lib/types";
import Image from "next/image";
import Link from "next/link";

const TYPE_LABELS: Record<SeriesType, string> = {
  tv: "Serie",
  movie: "Película",
  ova: "OVA",
  ona: "ONA",
  special: "Especial",
};

const STATUS_LABELS: Record<SeriesStatus, string> = {
  ongoing: "En emisión",
  completed: "Concluido",
  upcoming: "Por estrenar",
  hiatus: "En pausa",
};

const STATUS_COLORS: Record<SeriesStatus, string> = {
  ongoing: "bg-green-600",
  completed: "bg-blue-600",
  upcoming: "bg-amber-600",
  hiatus: "bg-neutral-600",
};

interface SeriesCardProps {
  series: Series;
}

export default function SeriesCard({ series }: SeriesCardProps) {
  return (
    <Link
      href={`/series/${series.slug}`}
      className="group block rounded-lg overflow-hidden bg-neutral-800 hover:ring-2 hover:ring-indigo-500 transition-all"
    >
      {/* Cover image — 2:3 aspect ratio */}
      <div className="relative aspect-[2/3] bg-neutral-700 overflow-hidden">
        {series.coverUrl ? (
          <Image
            src={series.coverUrl}
            alt={series.title}
            fill
            sizes="(max-width: 640px) 50vw, (max-width: 1024px) 25vw, 16vw"
            className="object-cover group-hover:scale-105 transition-transform duration-300"
          />
        ) : (
          <div className="w-full h-full flex items-center justify-center text-neutral-500 text-xs text-center px-2">
            No cover
          </div>
        )}

        {/* Type + Status badges (top) */}
        <div className="absolute top-1.5 left-1.5 flex flex-wrap gap-1">
          {series.type && (
            <span className="bg-indigo-600 text-white text-[10px] font-bold px-1.5 py-0.5 rounded">
              {TYPE_LABELS[series.type] ?? series.type}
            </span>
          )}
          {series.status && (
            <span className={`${STATUS_COLORS[series.status] ?? "bg-neutral-600"} text-white text-[10px] font-bold px-1.5 py-0.5 rounded`}>
              {STATUS_LABELS[series.status] ?? series.status}
            </span>
          )}
        </div>

        {series.score !== null && (
          <span className="absolute top-1.5 right-1.5 bg-black/70 text-indigo-400 text-xs font-semibold px-1.5 py-0.5 rounded">
            ★ {series.score.toFixed(1)}
          </span>
        )}
      </div>

      {/* Title */}
      <div className="p-2">
        <h3 className="text-sm font-medium text-white line-clamp-2 leading-snug">
          {series.title}
        </h3>
        {series.year && (
          <p className="text-xs text-neutral-500 mt-0.5">{series.year}</p>
        )}
      </div>
    </Link>
  );
}
