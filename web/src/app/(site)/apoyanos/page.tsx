import SectionHeader from "@/components/ui/SectionHeader";
import type { Metadata } from "next";
import Link from "next/link";

const CAFECITO_URL = process.env.NEXT_PUBLIC_CAFECITO_URL ?? "";
const KOFI_URL = process.env.NEXT_PUBLIC_KOFI_URL ?? "";

export const metadata: Metadata = {
  title: "Apoyanos",
  description:
    "SheicobAnime no tiene paywall. Mantenemos la publicidad al mínimo. Si esta página es tu manera de mirar anime, bancanos con un cafecito.",
  alternates: { canonical: "/apoyanos" },
};

export default function ApoyanosPage() {
  const hasAnyLink = Boolean(CAFECITO_URL || KOFI_URL);

  return (
    <div className="mx-auto max-w-2xl px-4 py-12">
      <SectionHeader size="lg" eyebrow="De fan a fan" title="Apoyanos" className="mb-6" />

      <div className="sh-body space-y-4 text-[15px]">
        <p>
          SheicobAnime no tiene paywall. Mantenemos la publicidad al mínimo y nunca te
          interrumpimos entre vos y el play: lo justo para que los servidores sigan
          prendidos.
        </p>
        <p>
          Esos servidores cuestan plata todos los meses. Si esta página es tu manera de
          mirar anime, podés bancarla con un <strong className="text-ink-1">cafecito</strong> —
          una vez, cuando quieras, sin suscripción. Cada aporte nos saca presión de tener
          que meter más anuncios.
        </p>
        <p>
          Quien dona se lleva el badge{" "}
          <span className="rounded-badge border border-[rgba(255,197,61,0.35)] bg-[rgba(255,197,61,0.14)] px-2 py-0.5 text-[12px] font-bold text-gold">
            MECENAS
          </span>{" "}
          en su perfil y comentarios. Es nuestra manera de decir gracias donde todos lo
          vean.
        </p>
      </div>

      <div className="mt-8 flex flex-wrap items-center gap-3">
        {CAFECITO_URL && (
          <Link
            href={CAFECITO_URL}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex h-12 items-center gap-2 rounded-btn px-6 text-[15px] font-bold text-[var(--text-on-accent)] shadow-glow transition-all duration-fast hover:brightness-110 active:scale-[0.97]"
            style={{ background: "var(--grad-action)" }}
          >
            ♥ Invitanos un cafecito
          </Link>
        )}
        {KOFI_URL && (
          <Link
            href={KOFI_URL}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex h-12 items-center gap-2 rounded-btn border border-line-2 bg-abyss-3 px-5 text-[15px] font-semibold text-ink-1 transition-all duration-fast hover:brightness-110 active:scale-[0.97]"
          >
            Ko-fi (desde el exterior)
          </Link>
        )}
        {!hasAnyLink && (
          <span className="text-sm text-ink-3">
            El link de donaciones se está configurando — volvé pronto.
          </span>
        )}
        <Link
          href="/"
          className="inline-flex h-12 items-center rounded-btn border border-line-2 bg-abyss-3 px-5 text-[15px] font-semibold text-ink-1 transition-all duration-fast hover:brightness-110"
        >
          Seguir mirando anime
        </Link>
      </div>

      <p className="mt-10 font-mono text-[11px] text-ink-3">
        100% de las donaciones va a infraestructura. Sin paywall, hoy y siempre.
      </p>
    </div>
  );
}
