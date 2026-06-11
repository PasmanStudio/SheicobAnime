import AdSlot from "@/components/ads/AdSlot";
import CommentSection from "@/components/comments/CommentSection";
import DirectEpisodePlayer from "@/components/player/DirectEpisodePlayer";
import EpisodeSidebar from "@/components/player/EpisodeSidebar";
import SectionHeader from "@/components/ui/SectionHeader";
import MarkWatchedButton from "@/components/watchlist/MarkWatchedButton";
import { ApiError, getEpisodeBySlug, getEpisodeMirrorsBySlug, getSeriesEpisodes } from "@/lib/api";
import { siteUrl } from "@/lib/site-url";
import type { Metadata } from "next";
import Image from "next/image";
import Link from "next/link";
import { notFound } from "next/navigation";

export const revalidate = 300;

interface Props {
  params: Promise<{ slug: string; episode: string }>;
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { slug, episode: episodeStr } = await params;
  const episodeNumber = Number(episodeStr);
  if (!Number.isInteger(episodeNumber) || episodeNumber < 1) {
    return { title: "Episode Not Found" };
  }

  try {
    const episode = await getEpisodeBySlug(slug, episodeNumber);
    const seriesTitle = episode.series?.title ?? "Anime";
    // SEO: title template "Ver {serie} EP {n} sub español online"
    const title = `Ver ${seriesTitle} EP ${episode.episodeNumber} sub español online`;
    const description = `Mirá ${seriesTitle} episodio ${episode.episodeNumber}${
      episode.title ? ` — ${episode.title}` : ""
    } online, subtitulado en español, gratis en SheicobAnime.`;
    const canonical = `/series/${slug}/${episode.episodeNumber}`;
    return {
      title,
      description,
      alternates: { canonical },
      openGraph: {
        title,
        description,
        url: canonical,
        images: episode.thumbnailUrl
          ? [{ url: episode.thumbnailUrl }]
          : episode.series?.coverUrl
            ? [{ url: episode.series.coverUrl }]
            : [],
      },
    };
  } catch {
    return { title: "Episode Not Found" };
  }
}

export default async function EpisodePage({ params }: Readonly<Props>) {
  const { slug, episode: episodeStr } = await params;
  const episodeNumber = Number(episodeStr);
  if (!Number.isInteger(episodeNumber) || episodeNumber < 1) notFound();

  let episode;
  try {
    episode = await getEpisodeBySlug(slug, episodeNumber);
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) notFound();
    throw err;
  }

  // Non-critical parallel fetches — never let failures 500 the page
  let allEpisodes: Awaited<ReturnType<typeof getSeriesEpisodes>>["data"] = [];
  let mirrors: Awaited<ReturnType<typeof getEpisodeMirrorsBySlug>> = [];
  try {
    [{ data: allEpisodes }, mirrors] = await Promise.all([
      getSeriesEpisodes(slug, { pageSize: 50 }),
      getEpisodeMirrorsBySlug(slug, episodeNumber),
    ]);
  } catch {
    allEpisodes = [];
    mirrors = [];
  }

  const episodeTitle = episode.title
    ? `Episodio ${episode.episodeNumber}: ${episode.title}`
    : `Episodio ${episode.episodeNumber}`;

  const pageUrl = `${siteUrl()}/series/${slug}/${episode.episodeNumber}`;
  const seriesUrl = `${siteUrl()}/series/${slug}`;
  const hasNext = allEpisodes.some((ep) => ep.episodeNumber === episodeNumber + 1);

  // ── JSON-LD: TVEpisode + BreadcrumbList ──────────────────────────────────
  const jsonLd = [
    {
      "@context": "https://schema.org",
      "@type": "TVEpisode",
      name: episode.title ?? `Episodio ${episode.episodeNumber}`,
      episodeNumber: episode.episodeNumber,
      url: pageUrl,
      ...(episode.thumbnailUrl ? { image: episode.thumbnailUrl } : {}),
      ...(episode.airedAt ? { datePublished: episode.airedAt } : {}),
      ...(episode.series
        ? {
            partOfSeries: {
              "@type": "TVSeries",
              name: episode.series.title,
              url: seriesUrl,
            },
          }
        : {}),
    },
    {
      "@context": "https://schema.org",
      "@type": "BreadcrumbList",
      itemListElement: [
        { "@type": "ListItem", position: 1, name: "Inicio", item: siteUrl() },
        ...(episode.series
          ? [{ "@type": "ListItem", position: 2, name: episode.series.title, item: seriesUrl }]
          : []),
        {
          "@type": "ListItem",
          position: episode.series ? 3 : 2,
          name: `Episodio ${episode.episodeNumber}`,
          item: pageUrl,
        },
      ],
    },
  ];

  return (
    <div className="mx-auto max-w-container px-4 py-6">
      <script
        type="application/ld+json"
        dangerouslySetInnerHTML={{
          __html: JSON.stringify(jsonLd).replace(/<\/script>/gi, "<\\/script>"),
        }}
      />

      {/* Breadcrumb — serie en cian, EP en mono */}
      {episode.series && (
        <nav className="mb-4 flex flex-wrap items-center gap-2 text-[13px]">
          <Link
            href={`/series/${episode.series.slug}`}
            className="font-semibold text-brand-bright hover:text-[var(--cyan-200)] transition-colors duration-fast truncate max-w-[220px] sm:max-w-none"
          >
            {episode.series.title}
          </Link>
          <span className="text-ink-3">/</span>
          <span className="sh-stat text-xs text-ink-2">
            EP {String(episodeNumber).padStart(2, "0")}
          </span>
        </nav>
      )}

      {/* ── 2-column layout: player area + sidebar ── */}
      <div className="flex flex-col lg:flex-row gap-5 items-start">
        {/* Left: Player + mirrors + título + comentarios */}
        <div className="flex-1 min-w-0 w-full">
          <DirectEpisodePlayer mirrors={mirrors} episodeTitle={episodeTitle} />

          {/* Título del episodio + nav EP anterior/siguiente — SIN ads en el medio */}
          <div className="mt-5 flex flex-wrap items-start justify-between gap-4">
            <div className="min-w-0">
              <h1 className="sh-display m-0 text-[clamp(17px,2.4vw,22px)]">
                EP {String(episodeNumber).padStart(2, "0")}
                {episode.title ? ` — ${episode.title}` : ""}
              </h1>
              <div className="mt-2 flex items-center gap-2.5 flex-wrap">
                {episode.series && (
                  <Link
                    href={`/series/${episode.series.slug}`}
                    className="text-[13px] font-semibold text-ink-2 hover:text-brand-bright transition-colors duration-fast"
                  >
                    {episode.series.title}
                  </Link>
                )}
                {episode.airedAt && (
                  <span className="sh-stat text-xs text-ink-3">
                    {new Date(episode.airedAt).toLocaleDateString("es-AR")}
                  </span>
                )}
                {episode.durationSecs !== null && (
                  <span className="sh-stat text-xs text-ink-3">
                    {Math.floor(episode.durationSecs / 60)} min
                  </span>
                )}
              </div>
            </div>

            <div className="flex items-center gap-2 shrink-0">
              {episode.id && (
                <MarkWatchedButton
                  episodeId={episode.id}
                  seriesSlug={slug}
                  episodeNumber={episodeNumber}
                  episodeTitle={episode.title}
                  seriesTitle={episode.series?.title}
                  coverUrl={episode.series?.coverUrl}
                />
              )}
              {episodeNumber > 1 && (
                <Link
                  href={`/series/${slug}/${episodeNumber - 1}`}
                  className="inline-flex h-9 items-center gap-1.5 rounded-btn border border-line-2 bg-abyss-3 px-3 text-[13px] font-semibold text-ink-1 transition-all duration-fast hover:brightness-110 active:scale-[0.97]"
                >
                  ‹ <span className="sh-stat text-xs">EP {episodeNumber - 1}</span>
                </Link>
              )}
              {hasNext && (
                <Link
                  href={`/series/${slug}/${episodeNumber + 1}`}
                  className="inline-flex h-9 items-center gap-1.5 rounded-btn px-3 text-[13px] font-bold text-[var(--text-on-accent)] transition-all duration-fast hover:brightness-110 active:scale-[0.97]"
                  style={{ background: "var(--grad-action)" }}
                >
                  <span className="sh-stat text-xs">EP {episodeNumber + 1}</span> ›
                </Link>
              )}
            </div>
          </div>

          {/* Serie info card */}
          {episode.series && (
            <div className="mt-5 flex items-start gap-4 rounded-card border border-line-1 bg-abyss-2 p-4">
              {episode.series.coverUrl && (
                <div className="relative h-20 w-14 shrink-0 overflow-hidden rounded-badge">
                  <Image
                    src={episode.series.coverUrl}
                    alt={episode.series.title}
                    fill
                    sizes="56px"
                    className="object-cover"
                  />
                </div>
              )}
              <div className="min-w-0">
                <Link
                  href={`/series/${episode.series.slug}`}
                  className="sh-title text-[15px] hover:text-[var(--cyan-200)] transition-colors duration-fast"
                >
                  {episode.series.title}
                </Link>
                <p className="mt-0.5 sh-stat text-xs text-ink-3">
                  {allEpisodes.length} episodios
                </p>
              </div>
            </div>
          )}

          {/* Ad DEBAJO del bloque de visionado — el mejor slot (doc 2) */}
          <div className="my-6">
            <AdSlot placement="episode_below_player" />
          </div>

          {/* Comments */}
          <section>
            <SectionHeader title="Comentarios" className="mb-4" />
            <CommentSection
              pageId={`${slug}-ep${episode.episodeNumber}`}
              pageUrl={pageUrl}
            />
          </section>

          {/* Único ad de cierre — entre comentarios y footer */}
          <AdSlot placement="episode_bottom" />
        </div>

        {/* Right: Episode sidebar — fixed width on desktop, full width stacked on mobile */}
        <div className="w-full lg:w-72 xl:w-80 shrink-0 lg:sticky lg:top-[84px]">
          <EpisodeSidebar
            episodes={allEpisodes}
            currentEpisodeNumber={episodeNumber}
            seriesSlug={slug}
            seriesTitle={episode.series?.title ?? "Serie"}
            seriesCoverUrl={episode.series?.coverUrl}
          />
        </div>
      </div>
    </div>
  );
}
