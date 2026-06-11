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
    <div className="group relative rounded-lg overflow-hidden bg-abyss-3 hover:ring-2 hover:ring-[var(--accent)] transition-all h-full flex flex-col">
      {/* Cover — 2:3 */}
      <div className="relative aspect-[2/3] bg-abyss-3 flex-shrink-0 overflow-hidden">
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
          <div className="w-full h-full flex items-center justify-center text-ink-3 text-xs text-center px-2">
            Sin portada
          </div>
        )}

        {/* Top badges */}
        <div className="absolute top-1.5 left-1.5 flex flex-wrap gap-1">
          <span className="bg-brand text-[var(--text-on-accent)] text-white text-[10px] font-bold px-1.5 py-0.5 rounded">
            {format}
          </span>
        </div>

        {/* Score */}
        {score && (
          <span className={`absolute top-1.5 right-1.5 bg-black/75 text-xs font-bold px-1.5 py-0.5 rounded ${
            parseFloat(score) >= 8 ? "text-green-400" :
            parseFloat(score) >= 6 ? "text-amber-400" :
                                     "text-ink-2"
          }`}>
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
            <div className="bg-abyss-2 text-ink-2 text-[10px] font-semibold px-2 py-1 text-center">
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
            <span className="text-[10px] text-ink-3">
              {media.episodes} ep
            </span>
          )}
          {media.studios.nodes[0] && (
            <span className="text-[10px] text-ink-3 line-clamp-1">
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
