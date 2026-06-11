"use client";

import type { ListSummary } from "@/lib/lists";
import { useSession } from "next-auth/react";
import { useEffect, useRef, useState } from "react";

interface Props {
  seriesSlug: string;
  seriesTitle: string;
  coverUrl?: string | null;
}

export default function AddToListButton({ seriesSlug, seriesTitle, coverUrl }: Props) {
  const { data: session, status } = useSession();
  const [open, setOpen] = useState(false);
  const [lists, setLists] = useState<ListSummary[]>([]);
  const [inLists, setInLists] = useState<string[]>([]);
  const [loadingLists, setLoadingLists] = useState(false);
  const [pendingId, setPendingId] = useState<string | null>(null);
  const [showNewInput, setShowNewInput] = useState(false);
  const [newName, setNewName] = useState("");
  const [creatingList, setCreatingList] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const newInputRef = useRef<HTMLInputElement>(null);

  // Close on outside click
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, []);

  const fetchLists = async () => {
    setLoadingLists(true);
    try {
      const res = await fetch(`/api/lists?seriesSlug=${encodeURIComponent(seriesSlug)}`);
      if (res.ok) {
        const data = (await res.json()) as { lists: ListSummary[]; in_lists: string[] };
        setLists(data.lists);
        setInLists(data.in_lists);
      }
    } catch {
      // silently fail
    } finally {
      setLoadingLists(false);
    }
  };

  const handleOpen = () => {
    if (!open) {
      fetchLists();
      setShowNewInput(false);
      setNewName("");
    }
    setOpen((v) => !v);
  };

  const handleToggle = async (listId: string) => {
    const isIn = inLists.includes(listId);
    setPendingId(listId);
    try {
      if (isIn) {
        await fetch(`/api/lists/${listId}/items?slug=${encodeURIComponent(seriesSlug)}`, {
          method: "DELETE",
        });
        setInLists((prev) => prev.filter((id) => id !== listId));
        setLists((prev) =>
          prev.map((l) =>
            l.id === listId ? { ...l, item_count: l.item_count - 1 } : l,
          ),
        );
      } else {
        await fetch(`/api/lists/${listId}/items`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ seriesSlug, seriesTitle, coverUrl: coverUrl ?? null }),
        });
        setInLists((prev) => [...prev, listId]);
        setLists((prev) =>
          prev.map((l) =>
            l.id === listId ? { ...l, item_count: l.item_count + 1 } : l,
          ),
        );
      }
    } catch {
      // silently fail
    } finally {
      setPendingId(null);
    }
  };

  const handleCreateAndAdd = async (e: React.FormEvent) => {
    e.preventDefault();
    const trimmed = newName.trim();
    if (!trimmed) return;
    setCreatingList(true);
    try {
      const res = await fetch("/api/lists", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name: trimmed }),
      });
      if (!res.ok) return;
      const list = (await res.json()) as ListSummary & { id: string };
      // Add the series to the new list
      await fetch(`/api/lists/${list.id}/items`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ seriesSlug, seriesTitle, coverUrl: coverUrl ?? null }),
      });
      setLists((prev) => [{ ...list, item_count: 1, preview_covers: [] }, ...prev]);
      setInLists((prev) => [...prev, list.id]);
      setShowNewInput(false);
      setNewName("");
    } catch {
      // silently fail
    } finally {
      setCreatingList(false);
    }
  };

  if (status === "loading") {
    return <div className="h-9 w-24 rounded-lg bg-abyss-3 animate-pulse" />;
  }
  if (!session?.user) return null;

  const addedCount = inLists.length;

  return (
    <div className="relative inline-block" ref={ref}>
      <button
        onClick={handleOpen}
        className={`flex items-center gap-1.5 px-3 py-2 rounded-lg border text-sm font-medium transition-colors
          hover:bg-abyss-3
          ${addedCount > 0
            ? "border-[var(--accent-border)] text-brand-bright"
            : "border-line-2 text-ink-2"
          }`}
        aria-haspopup="listbox"
        aria-expanded={open}
      >
        <span>📋</span>
        {addedCount > 0 ? `En ${addedCount} lista${addedCount > 1 ? "s" : ""}` : "Listas"}
        <svg className="w-3.5 h-3.5 shrink-0 opacity-60" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
        </svg>
      </button>

      {open && (
        <div
          className="absolute z-50 left-0 top-full mt-1 w-56 rounded-xl bg-abyss-2 border border-line-2 shadow-2xl overflow-hidden"
          role="listbox"
        >
          {loadingLists ? (
            <div className="px-3 py-4 text-center text-sm text-ink-3">
              <span className="w-4 h-4 inline-block rounded-full border-2 border-line-2 border-t-transparent animate-spin" />
            </div>
          ) : lists.length === 0 && !showNewInput ? (
            <div className="px-3 py-3 text-sm text-ink-3 text-center">
              No tenés listas creadas.
            </div>
          ) : (
            lists.map((list) => {
              const isIn = inLists.includes(list.id);
              const isPending = pendingId === list.id;
              return (
                <button
                  key={list.id}
                  role="option"
                  aria-selected={isIn}
                  disabled={isPending}
                  onClick={() => handleToggle(list.id)}
                  className={`w-full flex items-center gap-2 px-3 py-2.5 text-sm transition-colors
                    hover:bg-abyss-3 disabled:opacity-60
                    ${isIn ? "text-white" : "text-ink-2"}`}
                >
                  {isPending ? (
                    <span className="w-4 h-4 rounded-full border-2 border-current border-t-transparent animate-spin shrink-0" />
                  ) : (
                    <span
                      className={`w-4 h-4 rounded border shrink-0 flex items-center justify-center
                        ${isIn ? "bg-brand text-[var(--text-on-accent)] border-brand" : "border-line-2"}`}
                    >
                      {isIn && (
                        <svg className="w-2.5 h-2.5 text-white" fill="currentColor" viewBox="0 0 20 20">
                          <path fillRule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z" clipRule="evenodd" />
                        </svg>
                      )}
                    </span>
                  )}
                  <span className="truncate flex-1 text-left">{list.name}</span>
                  <span className="text-xs text-ink-3 shrink-0">{list.item_count}</span>
                </button>
              );
            })
          )}

          <div className="border-t border-line-1" />

          {showNewInput ? (
            <form onSubmit={handleCreateAndAdd} className="px-2 py-2 flex items-center gap-1.5">
              <input
                ref={newInputRef}
                type="text"
                value={newName}
                onChange={(e) => setNewName(e.target.value)}
                placeholder="Nombre…"
                maxLength={80}
                autoFocus
                className="bg-abyss-3 border border-line-2 rounded-md px-2 py-1 text-xs text-white placeholder-[var(--text-3)] outline-none flex-1 min-w-0"
                disabled={creatingList}
              />
              <button
                type="submit"
                disabled={creatingList || !newName.trim()}
                className="px-2 py-1 rounded-md bg-brand text-[var(--text-on-accent)] hover:brightness-110 disabled:opacity-50 text-white text-xs font-medium transition-colors shrink-0"
              >
                {creatingList ? (
                  <span className="w-3 h-3 inline-block rounded-full border-2 border-white border-t-transparent animate-spin" />
                ) : (
                  "OK"
                )}
              </button>
              <button
                type="button"
                onClick={() => { setShowNewInput(false); setNewName(""); }}
                className="text-ink-3 hover:text-ink-1 transition-colors text-sm leading-none shrink-0"
              >
                ✕
              </button>
            </form>
          ) : (
            <button
              onClick={() => {
                setShowNewInput(true);
                setTimeout(() => newInputRef.current?.focus(), 50);
              }}
              className="w-full flex items-center gap-2 px-3 py-2.5 text-sm text-ink-2 hover:text-white hover:bg-abyss-3 transition-colors"
            >
              <span className="w-4 text-center text-base leading-none">＋</span>
              Nueva lista
            </button>
          )}
        </div>
      )}
    </div>
  );
}
