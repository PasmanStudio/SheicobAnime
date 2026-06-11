"use client";

import { useRouter } from "next/navigation";
import { useRef, useState } from "react";

interface Props {
  listId: string;
  initialName: string;
  initialDescription: string | null;
}

export default function EditListNameButton({ listId, initialName, initialDescription }: Props) {
  const router = useRouter();
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState(initialName);
  const [description, setDescription] = useState(initialDescription ?? "");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const handleOpen = () => {
    setEditing(true);
    setError(null);
    setTimeout(() => inputRef.current?.focus(), 50);
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    const trimmedName = name.trim();
    if (!trimmedName) return;
    setLoading(true);
    setError(null);
    try {
      const res = await fetch(`/api/lists/${listId}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name: trimmedName, description: description.trim() || null }),
      });
      if (!res.ok) {
        const data = await res.json().catch(() => ({}));
        setError((data as { error?: string }).error ?? "Error al guardar");
        return;
      }
      setEditing(false);
      router.refresh();
    } catch {
      setError("Error de conexión");
    } finally {
      setLoading(false);
    }
  };

  if (!editing) {
    return (
      <button
        onClick={handleOpen}
        className="text-ink-3 hover:text-ink-1 transition-colors text-xs px-2 py-1 rounded-md hover:bg-abyss-3 border border-transparent hover:border-line-2 shrink-0"
        title="Renombrar lista"
      >
        ✏️ Editar
      </button>
    );
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-2 w-full max-w-md">
      <div className="flex items-center gap-2">
        <input
          ref={inputRef}
          type="text"
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="Nombre de la lista…"
          maxLength={80}
          className="bg-abyss-3 border border-line-2 rounded-lg px-3 py-1.5 text-sm text-white placeholder-[var(--text-3)] outline-none flex-1 focus:border-brand transition-colors"
          disabled={loading}
        />
        <button
          type="submit"
          disabled={loading || !name.trim()}
          className="px-3 py-1.5 rounded-lg bg-brand text-[var(--text-on-accent)] hover:brightness-110 disabled:opacity-50 text-white text-xs font-medium transition-colors shrink-0"
        >
          {loading ? (
            <span className="w-3.5 h-3.5 inline-block rounded-full border-2 border-white border-t-transparent animate-spin" />
          ) : (
            "Guardar"
          )}
        </button>
        <button
          type="button"
          onClick={() => { setEditing(false); setName(initialName); setDescription(initialDescription ?? ""); }}
          className="text-ink-3 hover:text-ink-1 transition-colors text-sm shrink-0"
        >
          ✕
        </button>
      </div>
      <input
        type="text"
        value={description}
        onChange={(e) => setDescription(e.target.value)}
        placeholder="Descripción (opcional)…"
        maxLength={200}
        className="bg-abyss-3 border border-line-2 rounded-lg px-3 py-1.5 text-xs text-ink-2 placeholder-[var(--text-3)] outline-none focus:border-brand transition-colors"
        disabled={loading}
      />
      {error && <p className="text-xs text-red-400">{error}</p>}
    </form>
  );
}
