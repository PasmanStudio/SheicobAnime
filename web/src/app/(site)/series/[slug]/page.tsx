import AdSlot from "@/components/ads/AdSlot";
import LikeButton from "@/components/likes/LikeButton";
import AddToListButton from "@/components/lists/AddToListButton";
import AddToTierButton from "@/components/tierlist/AddToTierButton";
import FollowButton from "@/components/series/FollowButton";
import GenreChip from "@/components/ui/GenreChip";
import Pagination from "@/components/ui/Pagination";
import ScoreBadge from "@/components/ui/ScoreBadge";
import SectionHeader from "@/components/ui/SectionHeader";
import EmptyState from "@/components/ui/EmptyState";
import StatusBadge from "@/components/ui/StatusBadge";
import WatchlistButton from "@/components/watchlist/WatchlistButton";
import { ApiError, getSeriesBySlug, getSeriesEpisodes } from "@/lib/api";
import { siteUrl } from "@/lib/site-url";
import type { Metadata } from "next";
import Image from "next/image";
import Link from "next/link";
import { notFound } from "next/navigation";
import { TYPE_LABELS } from "@/lib/labels";

export const revalidate = 300;

interface Props {
  params: Promise<{ slug: string }>;
  searchParams: Promise<{ page?: string }>;
}

export async function generateMetadata({ params }: Pick<Props, "params">): Promise<Metadata> {
  try {
    const { slug } = await params;
    const series = await getSeriesBySlug(slug);
    const desc =
      series.synopsis?.slice(0, 160) ??
      `Mira ${series.title} online en SheicobAnime. Episodios subtitulados en español.`;
    const canonical = `/series/${slug}`;
    return {
      title: series.title,
      description: desc,
      alternates: { canonical },
      openGraph: {
        title: series.title,
        description: desc,
        type: "video.tv_show",
        url: canonical,
        images: series.coverUrl
          ? [{ url: series.coverUrl, width: 460, height: 690, alt: series.title }]
          : [],
      },
      twitter: {
        card: "summary_large_image",
        title: series.title,
        description: desc,
        images: series.coverUrl ? [series.coverUrl] : [],
      },
    };
  } catch {
    return { title: "Series Not Found" };
  }
}

export default async function SeriesPage({ params, searchParams }: Props) {
  const { slug } = await params;
  const { page: pageParam } = await searchParams;
  const page = Math.max(1, parseInt(pageParam ?? "1", 10));

  let series, episodesPage;
  try {
    [series, episodesPage] = await Promise.all([
      getSeriesBySlug(slug),
      getSeriesEpisodes(slug, { page, pageSize: 24 }),
    ]);
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) notFound();
    throw err;
  }

  // ── JSON-LD structured data ────────────────────────────────────────────────
  const jsonLd: Record<string, unknown> = {
    "@context": "https://schema.org",
    "@type": series.type === "movie" ? "Movie" : "TVSeries",
    name: series.title,
    url: `${siteUrl()}/series/${slug}`,
    ...(series.titleRomaji && series.titleRomaji !== series.title
      ? { alternateName: series.titleRomaji }
      : {}),
    ...(series.synopsis ? { description: series.synopsis } : {}),
    ...(series.coverUrl ? { image: series.coverUrl } : {}),
    ...(series.genres.length > 0
      ? { genre: series.genres.map((g) => g.name) }
      : {}),
    ...(series.episodeCount ? { numberOfEpisodes: series.episodeCount } : {}),
    ...(series.year ? { startDate: String(series.year) } : {}),
    ...(series.language ? { inLanguage: series.language } : {}),
    ...(series.studio ? { productionCompany: { "@type": "Organization", name: series.studio } } : {}),
  };

  const heroImage = series.bannerUrl ?? series.coverUrl;

  const details: Array<[string, string]> = [];
  if (series.type) details.push(["Tipo", TYPE_LABELS[series.type] ?? series.type.toUpperCase()]);
  if (series.year) details.push(["Año", String(series.year)]);
  if (series.studio) details.push(["Estudio", series.studio]);
  if (series.season) details.push(["Temporada", series.season]);
  if (series.demographics) details.push(["Demografía", series.demographics]);
  if (series.episodeCount) details.push(["Episodios", String(series.episodeCount)]);
  if (series.durationMinutes !== null) details.push(["Duración", `${series.durationMinutes} min`]);
  if (series.airedDate) details.push(["Emitido", series.airedDate]);
  if (series.language) details.push(["Idioma", series.language]);
  if (series.quality) details.push(["Calidad", series.quality]);

  return (
    <>
      <script
        type="application/ld+json"
        dangerouslySetInnerHTML={{
          __html: JSON.stringify(jsonLd).replace(/<\/script>/gi, "<\\/script>"),
        }}
      />

      {/* ── Hero: poster blureado de fondo + protección hacia el abismo ── */}
      <div className="relative border-b border-line-1">
        {heroImage && (
          <div className="absolute inset-0 overflow-hidden">
            <Image
              src={heroImage}
              alt=""
              fill
              sizes="100vw"
              className="object-cover opacity-50 blur-[28px] saturate-[1.2] scale-[1.2]"
              priority
            />
            <div
              className="absolute inset-0"
              style={{
                background:
                  "linear-gradient(to top, var(--bg-0) 4%, rgba(7,9,14,0.78) 60%, rgba(7,9,14,0.6) 100%)",
              }}
            />
          </div>
        )}

        <div className="relative mx-auto max-w-container px-4 pt-10 pb-8 flex flex-col md:flex-row gap-6 md:gap-8 items-center md:items-end">
          {/* Poster */}
          <div className="shrink-0 w-36 sm:w-44 md:w-[190px]">
            <div className="relative aspect-[2/3] rounded-card overflow-hidden bg-abyss-3 border border-line-2 shadow-overlay">
              {series.coverUrl ? (
                <Image
                  src={series.coverUrl}
                  alt={series.title}
                  fill
                  sizes="(max-width: 768px) 44vw, 190px"
                  className="object-cover"
                  priority
                />
              ) : (
                <div className="flex h-full w-full items-center justify-center font-display text-5xl italic font-black text-[rgba(255,255,255,0.14)]">
                  {series.title.trim()[0]?.toUpperCase() ?? "?"}
                </div>
              )}
            </div>
          </div>

          {/* Info */}
          <div className="flex-1 flex flex-col gap-3 pb-1 text-center md:text-left items-center md:items-start">
            <div className="flex items-center gap-2.5 flex-wrap justify-center md:justify-start">
              {series.type && (
                <span className="rounded-badge px-2 py-[3px] text-[11px] font-bold bg-[var(--accent-muted)] text-brand-bright border border-[var(--accent-border)]">
                  {TYPE_LABELS[series.type] ?? series.type}
                </span>
              )}
              <StatusBadge status={series.status} />
              <span className="sh-stat text-xs text-ink-3">
                {[
                  series.year,
                  series.episodeCount ? `${series.episodeCount} episodios` : null,
                  series.durationMinutes ? `${series.durationMinutes} min` : null,
                ]
                  .filter(Boolean)
                  .join(" · ")}
              </span>
            </div>

            <h1 className="sh-display m-0 !text-[clamp(24px,3.4vw,38px)]">{series.title}</h1>

            {series.titleRomaji && series.titleRomaji !== series.title && (
              <p className="m-0 text-sm text-ink-3">{series.titleRomaji}</p>
            )}

            <div className="flex items-center gap-3 flex-wrap justify-center md:justify-start">
              <ScoreBadge score={series.score} size="lg" />
              {series.studio && <span className="text-[13px] text-ink-2">{series.studio}</span>}
              {series.genres.length > 0 && (
                <div className="flex gap-1.5 flex-wrap justify-center md:justify-start">
                  {series.genres.map((g) => (
                    <GenreChip key={g.id} name={g.name} />
                  ))}
                </div>
              )}
            </div>

            {series.synopsis && (
              <p className="sh-body m-0 max-w-2xl text-sm">{series.synopsis}</p>
            )}

            {/* Action buttons */}
            <div className="flex flex-wrap gap-2 mt-1 justify-center md:justify-start">
              <FollowButton seriesSlug={slug} />
              <LikeButton
                seriesSlug={slug}
                seriesTitle={series.title}
                coverUrl={series.coverUrl}
              />
              <WatchlistButton
                seriesSlug={slug}
                seriesTitle={series.title}
                coverUrl={series.coverUrl}
              />
              <AddToListButton
                seriesSlug={slug}
                seriesTitle={series.title}
                coverUrl={series.coverUrl}
              />
              <AddToTierButton
                seriesSlug={slug}
                seriesTitle={series.title}
                coverUrl={series.coverUrl}
              />
            </div>
          </div>
        </div>
      </div>

      <div className="mx-auto max-w-container px-4 py-8">
        {/* Episodes */}
        <section>
          <SectionHeader
            title="Episodios"
            eyebrow={
              series.episodeCount ? `${series.episodeCount} en total` : undefined
            }
            className="mb-5"
          />

          {episodesPage.data.length === 0 ? (
            <EmptyState
              title="Todavía no hay episodios"
              description="Súmala a tu lista y te va a aparecer apenas salga el primero."
              cta={{ href: "/temporada", label: "Ver la temporada" }}
            />
          ) : (
            <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2.5">
              {episodesPage.data.map((ep) => (
                <Link
                  key={ep.id}
                  href={`/series/${slug}/${ep.episodeNumber}`}
                  className="flex items-center gap-3.5 rounded-card border border-line-1 bg-abyss-2 px-3 py-2.5 transition-all duration-fast hover:border-line-2 hover:bg-abyss-3"
                >
                  <span className="sh-stat min-w-[52px] text-[13px] text-brand-bright">
                    EP {String(ep.episodeNumber).padStart(2, "0")}
                  </span>
                  <span className="flex-1 truncate text-[13px] font-medium text-ink-1">
                    {ep.title || `Episodio ${ep.episodeNumber}`}
                  </span>
                  {ep.airedAt && (
                    <span className="sh-stat text-[10px] text-ink-3">
                      {formatShortDate(ep.airedAt)}
                    </span>
                  )}
                </Link>
              ))}
            </div>
          )}

          <Pagination
            page={page}
            total={episodesPage.total}
            pageSize={24}
            basePath={`/series/${slug}`}
          />
        </section>

        {/* Ad — después del bloque de contenido, antes de detalles */}
        <div className="mt-9">
          <AdSlot placement="series_below_info" />
        </div>

        {/* Detalles */}
        {details.length > 0 && (
          <section className="mt-9">
            <SectionHeader title="Detalles" className="mb-5" />
            <dl className="m-0 grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 gap-x-6 gap-y-4 max-w-3xl">
              {details.map(([k, v]) => (
                <div key={k}>
                  <dt className="font-mono text-[10px] uppercase tracking-[0.1em] text-ink-3">
                    {k}
                  </dt>
                  <dd className="m-0 mt-1 text-sm text-ink-1">{v}</dd>
                </div>
              ))}
            </dl>
          </section>
        )}
      </div>
    </>
  );
}

const MONTHS_SHORT = ["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic"];

function formatShortDate(iso: string): string {
  const d = new Date(iso);
  return `${d.getDate()} ${MONTHS_SHORT[d.getMonth()]}`;
}
