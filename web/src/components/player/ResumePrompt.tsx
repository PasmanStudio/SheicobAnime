"use client";

/**
 * Resume-playback modal shown when a viewer has prior progress on an episode
 * between 10s and (duration - 30s). Mirrors the JKAnime "Un momento!" UX.
 */
interface ResumePromptProps {
  readonly positionSeconds: number;
  readonly onAccept: () => void;
  readonly onCancel: () => void;
}

export default function ResumePrompt({
  positionSeconds,
  onAccept,
  onCancel,
}: ResumePromptProps) {
  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-labelledby="resume-title"
      className="absolute inset-0 z-30 flex items-center justify-center bg-black/80 backdrop-blur-sm"
    >
      <div className="bg-neutral-900 border border-neutral-700 rounded-lg shadow-2xl max-w-md w-[90%] p-6 space-y-5">
        <div className="space-y-2">
          <h2 id="resume-title" className="text-2xl font-bold text-white">
            ¡Un momento!
          </h2>
          <p className="text-sm text-neutral-300">
            Parece que ya estabas viendo este episodio. ¿Quieres continuar
            desde donde lo dejaste?
          </p>
          <p className="text-lg font-mono text-orange-400">
            {formatTime(positionSeconds)}
          </p>
        </div>

        <div className="flex gap-2 justify-end">
          <button
            type="button"
            onClick={onCancel}
            className="min-h-[44px] px-4 py-2 rounded bg-neutral-800 text-neutral-200 hover:bg-neutral-700 transition-colors focus:outline-none focus:ring-2 focus:ring-neutral-500"
          >
            Cancelar
          </button>
          <button
            type="button"
            onClick={onAccept}
            autoFocus
            className="min-h-[44px] px-4 py-2 rounded bg-orange-500 text-white font-semibold hover:bg-orange-400 transition-colors focus:outline-none focus:ring-2 focus:ring-orange-300"
          >
            Aceptar
          </button>
        </div>
      </div>
    </div>
  );
}

function formatTime(sec: number): string {
  if (!Number.isFinite(sec) || sec < 0) return "0:00";
  const h = Math.floor(sec / 3600);
  const m = Math.floor((sec % 3600) / 60);
  const s = Math.floor(sec % 60);
  const pad = (n: number) => n.toString().padStart(2, "0");
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`;
}
