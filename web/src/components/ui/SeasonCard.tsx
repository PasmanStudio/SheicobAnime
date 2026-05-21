import {
  formatAniListFormat,
  formatScore,
  type AniListMedia,
} from "@/lib/anilist";
import type { Series } from "@/lib/types";
import Image from "next/image";
import Link from "next/link";

interface SeasonCardProps {
  media: AniListMedia;
  /** Matched series from our DB, or null if not indexed */
  match: Series | null;
}

export default function SeasonCard({ media, match }: SeasonCardProps) {
  const title =
    media.title.romaji ?? media.title.english ?? media.title.native ?? "Unknown";
  const coverUrl =
    media.coverImage.extraLarge ?? media.coverImage.large ?? null;
  const score = formatScore(media.averageScore);
  const format = formatAniListFormat(media.format);
  const isAvailable = match !== null;

  const cardContent = (
    <div className="group relative rounded-lg overflow-hidden bg-neutral-800 hover:ring-2 hover:ring-indigo-500 transition-all h-full flex flex-col">
      {/* Cover — 2:3 */}
      <div className="relative aspect-[2/3] bg-neutral-700 flex-shrink-0 overflow-hidden">
        {coverUrl ? (
          <Image
            src={coverUrl}
            alt={title}
            fill
            sizes="(max-width: 640px) 50vw, (max-width: 1024px) 25vw, 16vw"
            className={`object-cover transition-transform duration-300 ${
              isAvailable
                ? "group-hover:scale-105"
                : "grayscale-[30%] group-hover:scale-102"
            }`}
          />
        ) : (
          <div className="w-full h-full flex items-center justify-center text-neutral-500 text-xs text-center px-2">
            Sin portada
          </div>
        )}

        {/* Top badges */}
        <div className="absolute top-1.5 left-1.5 flex flex-wrap gap-1">
          <span className="bg-indigo-600 text-white text-[10px] font-bold px-1.5 py-0.5 rounded">
            {format}
          </span>
        </div>

        {/* Score */}
        {score && (
          <span className="absolute top-1.5 right-1.5 bg-black/70 text-indigo-400 text-xs font-semibold px-1.5 py-0.5 rounded">
            ★ {score}
          </span>
        )}

        {/* Availability badge — bottom overlay */}
        <div className="absolute bottom-0 left-0 right-0">
          {isAvailable ? (
            <div className="bg-green-600/90 text-white text-[10px] font-bold px-2 py-1 text-center">
              ✓ Disponible
            </div>
          ) : (
            <div className="bg-neutral-900/80 text-neutral-400 text-[10px] font-semibold px-2 py-1 text-center">
              No indexado
            </div>
          )}
        </div>
      </div>

      {/* Info */}
      <div className="p-2 flex flex-col gap-1 flex-1">
        <h3 className="text-sm font-medium text-white line-clamp-2 leading-snug">
          {title}
        </h3>

        <div className="flex flex-wrap gap-1 mt-auto">
          {media.episodes && (
            <span className="text-[10px] text-neutral-500">
              {media.episodes} ep
            </span>
          )}
          {media.studios.nodes[0] && (
            <span className="text-[10px] text-neutral-500 line-clamp-1">
              · {media.studios.nodes[0].name}
            </span>
          )}
        </div>
      </div>
    </div>
  );

  if (isAvailable) {
    return (
      <Link href={`/series/${match.slug}`} className="block h-full">
        {cardContent}
      </Link>
    );
  }

  return <div className="h-full opacity-70">{cardContent}</div>;
}
