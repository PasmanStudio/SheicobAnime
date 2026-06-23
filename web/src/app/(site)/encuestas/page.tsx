import SeasonPoll from "@/components/polls/SeasonPoll";
import SectionHeader from "@/components/ui/SectionHeader";
import type { Metadata } from "next";

export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "Encuesta de temporada",
  description: "Vota el mejor estreno de la temporada en SheicobAnime.",
  alternates: { canonical: "/encuestas" },
};

export default function EncuestasPage() {
  return (
    <div className="mx-auto max-w-2xl px-4 py-8">
      <SectionHeader
        size="lg"
        eyebrow="Vota con la comunidad"
        title="Encuesta de temporada"
        className="mb-6"
      />
      <SeasonPoll />
    </div>
  );
}
