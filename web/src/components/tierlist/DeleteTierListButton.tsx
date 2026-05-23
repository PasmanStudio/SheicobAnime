"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";

interface Props {
  tierListId: string;
  tierListName: string;
}

export default function DeleteTierListButton({ tierListId, tierListName }: Props) {
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleDelete() {
    setLoading(true);
    setError(null);
    try {
      const res = await fetch(`/api/tierlist/${tierListId}`, { method: "DELETE" });
      if (!res.ok) throw new Error("Error al borrar");
      setOpen(false);
      router.refresh();
    } catch {
      setError("No se pudo borrar. Intentá de nuevo.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <>
      {/* Trash icon button — sits on top of the card link */}
      <button
        onClick={(e) => {
          e.preventDefault();
          e.stopPropagation();
          setOpen(true);
        }}
        title="Borrar tier list"
        className="absolute top-2 right-2 z-10 p-1.5 rounded-md bg-neutral-900/80 text-neutral-500 hover:text-red-400 hover:bg-neutral-800 transition-colors opacity-0 group-hover:opacity-100 focus:opacity-100"
        aria-label="Borrar tier list"
      >
        <svg className="w-4 h-4" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
          <polyline points="3 6 5 6 21 6" />
          <path d="M19 6l-1 14H6L5 6" />
          <path d="M10 11v6M14 11v6" />
          <path d="M9 6V4h6v2" />
        </svg>
      </button>

      {/* Confirmation modal */}
      {open && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4"
          onClick={() => !loading && setOpen(false)}
        >
          <div
            className="bg-neutral-900 border border-neutral-700 rounded-2xl p-6 max-w-sm w-full shadow-xl"
            onClick={(e) => e.stopPropagation()}
          >
            <h2 className="text-lg font-bold text-white mb-1">¿Borrar tier list?</h2>
            <p className="text-sm text-neutral-400 mb-1 break-words">
              Se va a borrar <span className="text-neutral-200 font-medium">{tierListName}</span> y todos sus animes.
            </p>
            <p className="text-xs text-neutral-600 mb-5">Esta acción no se puede deshacer.</p>

            {error && <p className="text-sm text-red-400 mb-4">{error}</p>}

            <div className="flex gap-3">
              <button
                onClick={() => setOpen(false)}
                disabled={loading}
                className="flex-1 px-4 py-2 rounded-xl border border-neutral-700 text-neutral-300 hover:bg-neutral-800 transition-colors text-sm disabled:opacity-50"
              >
                Cancelar
              </button>
              <button
                onClick={handleDelete}
                disabled={loading}
                className="flex-1 px-4 py-2 rounded-xl bg-red-600 hover:bg-red-500 text-white font-semibold text-sm transition-colors disabled:opacity-50 flex items-center justify-center gap-2"
              >
                {loading ? (
                  <svg className="w-4 h-4 animate-spin" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
                    <circle cx="12" cy="12" r="10" strokeOpacity={0.3} />
                    <path d="M12 2a10 10 0 0 1 10 10" />
                  </svg>
                ) : null}
                {loading ? "Borrando…" : "Borrar"}
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
