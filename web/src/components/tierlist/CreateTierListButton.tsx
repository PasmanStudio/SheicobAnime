"use client";

import { useRouter } from "next/navigation";
import { useRef, useState } from "react";

export default function CreateTierListButton() {
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const handleOpen = () => {
    setOpen(true);
    setError(null);
    setName("");
    setTimeout(() => inputRef.current?.focus(), 50);
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    const trimmed = name.trim();
    if (!trimmed) return;
    setLoading(true);
    setError(null);
    try {
      const res = await fetch("/api/tierlist", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name: trimmed }),
      });
      if (!res.ok) {
        const data = await res.json().catch(() => ({}));
        setError((data as { error?: string }).error ?? "Error al crear");
        return;
      }
      const list = (await res.json()) as { id: string };
      router.push(`/tierlist/${list.id}`);
    } catch {
      setError("Error de conexión");
    } finally {
      setLoading(false);
    }
  };

  if (!open) {
    return (
      <button
        onClick={handleOpen}
        className="flex items-center gap-2 px-4 py-2 rounded-lg bg-indigo-600 hover:bg-indigo-500 text-white text-sm font-medium transition-colors"
      >
        <span className="text-base leading-none">＋</span>
        Nueva tier list
      </button>
    );
  }

  return (
    <form
      onSubmit={handleSubmit}
      className="flex items-center gap-2 bg-neutral-800 border border-neutral-700 rounded-xl px-3 py-2"
    >
      <input
        ref={inputRef}
        type="text"
        value={name}
        onChange={(e) => setName(e.target.value)}
        placeholder="Nombre de la tier list…"
        maxLength={80}
        className="bg-transparent text-sm text-white placeholder-neutral-500 outline-none flex-1 min-w-0"
        disabled={loading}
      />
      {error && <span className="text-xs text-red-400 shrink-0">{error}</span>}
      <button
        type="submit"
        disabled={loading || !name.trim()}
        className="px-3 py-1 rounded-lg bg-indigo-600 hover:bg-indigo-500 disabled:opacity-50 text-white text-xs font-medium transition-colors shrink-0"
      >
        {loading ? (
          <span className="w-3.5 h-3.5 inline-block rounded-full border-2 border-white border-t-transparent animate-spin" />
        ) : (
          "Crear"
        )}
      </button>
      <button
        type="button"
        onClick={() => setOpen(false)}
        className="text-neutral-500 hover:text-neutral-300 transition-colors text-lg leading-none shrink-0"
        aria-label="Cancelar"
      >
        ✕
      </button>
    </form>
  );
}
