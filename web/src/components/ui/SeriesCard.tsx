import Link from "next/link";
import Image from "next/image";
import type { Series } from "@/lib/types";

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
