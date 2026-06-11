import type { Metadata } from "next";
import { getGenres } from "@/lib/api";

export const dynamic = "force-dynamic";
import GenreChip from "@/components/ui/GenreChip";
import SectionHeader from "@/components/ui/SectionHeader";

export const metadata: Metadata = {
  title: "Géneros",
  description: "Explorá anime por género en SheicobAnime.",
};

export default async function GenresIndexPage() {
  const genres = await getGenres();

  return (
    <div className="mx-auto max-w-container px-4 py-8 space-y-6">
      <SectionHeader size="lg" title="Explorá por género" />

      <div className="flex flex-wrap gap-2.5">
        {genres.map((g) => (
          <GenreChip key={g.id} name={g.name} className="!text-sm !px-4 !py-2" />
        ))}
      </div>
    </div>
  );
}
