"use client";

import { reportMirrorFailure } from "@/lib/api";
import type { Mirror } from "@/lib/types";
import { useMemo, useState } from "react";

interface Props {
  mirrors: Mirror[];
  episodeTitle: string;
}

function FlameIcon() {
  return (
    <svg
      width="13"
      height="13"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden
    >
      <path d="M8.5 14.5A2.5 2.5 0 0 0 11 12c0-1.38-.5-2-1-3-1.072-2.143-.224-4.054 2-6 .5 2.5 2 4.9 4 6.5 2 1.6 3 3.5 3 5.5a7 7 0 1 1-14 0c0-1.153.433-2.294 1-3a2.5 2.5 0 0 0 2.5 2.5z" />
    </svg>
  );
}

/**
 * Simple iframe-based episode player.
 * - SeekStreaming mirrors are branded "Sheicob" (shown first, flame icon)
 * - Other servers are collapsed under "Ver otros servidores"
 * - Mirrors como chips del design system; NUNCA un ad entre el player y estos controles
 */
export default function DirectEpisodePlayer({ mirrors, episodeTitle }: Readonly<Props>) {
  const activeMirrors = useMemo(
    () => [...mirrors].filter((m) => m.isActive).sort((a, b) => a.priority - b.priority),
    [mirrors],
  );

  const [selectedId, setSelectedId] = useState<string | null>(() => {
    const seek = activeMirrors.find((m) => m.providerName === "seekstreaming");
    return seek?.id ?? activeMirrors[0]?.id ?? null;
  });
  const [showOthers, setShowOthers] = useState(false);
  const [reportedIds, setReportedIds] = useState<Set<string>>(new Set());

  async function handleReport(mirrorId: string) {
    if (reportedIds.has(mirrorId)) return;
    setReportedIds((prev) => new Set(prev).add(mirrorId));
    try {
      await reportMirrorFailure(mirrorId);
    } catch {
      // best-effort — el reporte nunca bloquea el visionado
    }
  }

  const selected = activeMirrors.find((m) => m.id === selectedId) ?? activeMirrors[0] ?? null;
  const sheicobMirrors = activeMirrors.filter((m) => m.providerName === "seekstreaming");
  const otherMirrors = activeMirrors.filter((m) => m.providerName !== "seekstreaming");

  if (!selected) {
    return (
      <div className="aspect-video w-full bg-abyss-1 flex items-center justify-center rounded-modal border border-line-1">
        <div className="text-sm text-ink-3 text-center px-4">
          <p>Todavía no hay enlaces disponibles para este episodio.</p>
          <p className="mt-1">Probá de nuevo en un rato — los mirrors se cargan apenas salen.</p>
        </div>
      </div>
    );
  }

  const chipClass = (active: boolean) =>
    `inline-flex items-center gap-1.5 px-3 py-[7px] rounded-btn text-[13px] font-semibold transition-all duration-fast focus:outline-none focus-visible:shadow-focus ${
      active
        ? "bg-[var(--accent-muted)] text-brand-bright border border-[var(--accent-border)]"
        : "bg-abyss-2 text-ink-2 border border-line-1 hover:border-line-2 hover:text-ink-1"
    }`;

  return (
    <div>
      {/* ── Player ── */}
      <div className="aspect-video w-full bg-black rounded-t-modal overflow-hidden border border-b-0 border-line-2">
        <iframe
          key={selected.id}
          src={selected.embedUrl}
          title={episodeTitle}
          className="w-full h-full border-0 block"
          allowFullScreen
          allow="autoplay; fullscreen; encrypted-media; picture-in-picture"
          referrerPolicy="no-referrer"
        />
      </div>

      {/* ── Mirror selector — pegado al player, sin ads en el medio ── */}
      <div className="bg-abyss-1 rounded-b-modal border border-t-0 border-line-2 overflow-hidden">
        <div className="px-3 sm:px-4 py-3 flex flex-wrap items-center gap-2">
          <span className="sh-label shrink-0 mr-1">Mirrors</span>

          {/* Sheicob (seekstreaming) buttons */}
          {sheicobMirrors.length > 0 ? (
            sheicobMirrors.map((m) => (
              <button
                key={m.id}
                onClick={() => setSelectedId(m.id)}
                aria-pressed={selectedId === m.id}
                className={chipClass(selectedId === m.id)}
              >
                Sheicob
                {m.qualityLabel > 0 && (
                  <span className="sh-stat text-[10px] opacity-80">{m.qualityLabel}p</span>
                )}
                <FlameIcon />
              </button>
            ))
          ) : (
            /* No seekstreaming — show first other mirror as primary */
            otherMirrors.slice(0, 1).map((m) => (
              <button
                key={m.id}
                onClick={() => setSelectedId(m.id)}
                aria-pressed={selectedId === m.id}
                className={`${chipClass(selectedId === m.id)} capitalize`}
              >
                {m.providerName}
                {m.qualityLabel > 0 && (
                  <span className="sh-stat text-[10px] opacity-80">{m.qualityLabel}p</span>
                )}
              </button>
            ))
          )}

          {/* Toggle other servers — pushed right on wide screens */}
          {otherMirrors.length > 0 && (
            <button
              onClick={() => setShowOthers((v) => !v)}
              className="sm:ml-auto px-3 py-[7px] rounded-btn text-xs text-ink-3 hover:text-ink-1 bg-abyss-2 border border-line-1 hover:border-line-2 transition-all duration-fast focus:outline-none focus-visible:shadow-focus whitespace-nowrap"
            >
              Ver otros servidores {showOthers ? "▲" : "▼"}
            </button>
          )}
        </div>

        {/* Other servers (collapsed by default) */}
        {showOthers && otherMirrors.length > 0 && (
          <div className="px-3 sm:px-4 pb-3 pt-2 flex flex-wrap gap-2 border-t border-line-1">
            {otherMirrors.map((m) => (
              <button
                key={m.id}
                onClick={() => setSelectedId(m.id)}
                aria-pressed={selectedId === m.id}
                className={`${chipClass(selectedId === m.id)} capitalize`}
              >
                {m.providerName}
                {m.qualityLabel > 0 && (
                  <span className="sh-stat text-[10px] opacity-80">{m.qualityLabel}p</span>
                )}
              </button>
            ))}
          </div>
        )}

        {/* Reportar mirror caído — reporta el mirror seleccionado */}
        <div className="flex items-center gap-2 border-t border-line-1 px-3 sm:px-4 py-2">
          {reportedIds.has(selected.id) ? (
            <span className="text-xs text-[var(--success)]">
              Gracias por avisar — lo revisamos. Mientras tanto, probá otro mirror.
            </span>
          ) : (
            <button
              onClick={() => handleReport(selected.id)}
              className="text-xs text-ink-3 hover:text-[var(--danger)] transition-colors duration-fast"
            >
              ¿El video no anda? Reportá este mirror
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
