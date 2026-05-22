"use client";

import { useRef, useState, useTransition } from "react";
import { updateProfile } from "./actions";

interface Props {
  currentName: string | null;
  currentUsername: string | null;
  currentBio: string | null;
}

export default function ProfileEditModal({ currentName, currentUsername, currentBio }: Props) {
  const [open, setOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [isPending, startTransition] = useTransition();
  const formRef = useRef<HTMLFormElement>(null);

  function handleSubmit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const data = new FormData(e.currentTarget);
    setError(null);
    startTransition(async () => {
      const result = await updateProfile(data);
      if (result.ok) {
        setOpen(false);
        // Reload to reflect updated name/username in the page
        window.location.reload();
      } else {
        setError(result.error ?? "Error desconocido.");
      }
    });
  }

  return (
    <>
      {/* Trigger button */}
      <button
        onClick={() => setOpen(true)}
        className="shrink-0 px-4 py-1.5 rounded-lg border border-neutral-700 text-sm text-neutral-300 hover:text-white hover:border-neutral-500 transition-colors"
      >
        Editar perfil
      </button>

      {/* Modal overlay */}
      {open && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 px-4"
          onClick={(e) => { if (e.target === e.currentTarget) setOpen(false); }}
        >
          <div className="w-full max-w-md rounded-2xl bg-neutral-900 border border-neutral-700 shadow-2xl p-6">
            <div className="flex items-center justify-between mb-5">
              <h2 className="text-lg font-semibold text-white">Editar perfil</h2>
              <button
                onClick={() => setOpen(false)}
                className="text-neutral-500 hover:text-white transition-colors text-xl leading-none"
              >
                ✕
              </button>
            </div>

            <form ref={formRef} onSubmit={handleSubmit} className="space-y-4">
              {/* Display name */}
              <div>
                <label className="block text-xs text-neutral-400 mb-1.5 font-medium">
                  Nombre visible
                </label>
                <input
                  name="name"
                  type="text"
                  defaultValue={currentName ?? ""}
                  maxLength={60}
                  placeholder="Tu nombre"
                  className="w-full bg-neutral-800 border border-neutral-700 rounded-lg px-3 py-2 text-sm text-white placeholder-neutral-500 focus:outline-none focus:border-indigo-500 transition-colors"
                />
              </div>

              {/* Username */}
              <div>
                <label className="block text-xs text-neutral-400 mb-1.5 font-medium">
                  Nombre de usuario <span className="text-neutral-600">(solo letras, números, _ y .)</span>
                </label>
                <div className="flex items-center bg-neutral-800 border border-neutral-700 rounded-lg px-3 py-2 focus-within:border-indigo-500 transition-colors">
                  <span className="text-neutral-500 text-sm mr-1">@</span>
                  <input
                    name="username"
                    type="text"
                    defaultValue={currentUsername ?? ""}
                    maxLength={30}
                    placeholder="tu_usuario"
                    pattern="[a-zA-Z0-9_.\-]*"
                    className="flex-1 bg-transparent text-sm text-white placeholder-neutral-500 focus:outline-none"
                  />
                </div>
              </div>

              {/* Bio */}
              <div>
                <label className="block text-xs text-neutral-400 mb-1.5 font-medium">
                  Bio <span className="text-neutral-600">(máx. 300 caracteres)</span>
                </label>
                <textarea
                  name="bio"
                  defaultValue={currentBio ?? ""}
                  maxLength={300}
                  rows={3}
                  placeholder="Contá algo sobre vos..."
                  className="w-full bg-neutral-800 border border-neutral-700 rounded-lg px-3 py-2 text-sm text-white placeholder-neutral-500 focus:outline-none focus:border-indigo-500 transition-colors resize-none"
                />
              </div>

              {/* Error */}
              {error && (
                <p className="text-sm text-red-400 bg-red-950/40 rounded-lg px-3 py-2">
                  {error}
                </p>
              )}

              {/* Actions */}
              <div className="flex gap-3 pt-1">
                <button
                  type="button"
                  onClick={() => setOpen(false)}
                  className="flex-1 py-2 rounded-lg border border-neutral-700 text-sm text-neutral-400 hover:text-white hover:border-neutral-500 transition-colors"
                >
                  Cancelar
                </button>
                <button
                  type="submit"
                  disabled={isPending}
                  className="flex-1 py-2 rounded-lg bg-indigo-600 hover:bg-indigo-500 disabled:opacity-50 disabled:cursor-not-allowed text-sm text-white font-medium transition-colors"
                >
                  {isPending ? "Guardando…" : "Guardar cambios"}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </>
  );
}
