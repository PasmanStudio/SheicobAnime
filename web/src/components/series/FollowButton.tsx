"use client";

import LoginModal from "@/components/auth/LoginModal";
import { useSession } from "next-auth/react";
import { useEffect, useState } from "react";

interface Props {
  seriesSlug: string;
}

const VAPID_PUBLIC_KEY = process.env.NEXT_PUBLIC_VAPID_PUBLIC_KEY ?? "";

function urlBase64ToUint8Array(base64: string): Uint8Array {
  const padding = "=".repeat((4 - (base64.length % 4)) % 4);
  const raw = atob((base64 + padding).replace(/-/g, "+").replace(/_/g, "/"));
  const arr = new Uint8Array(raw.length);
  for (let i = 0; i < raw.length; i++) arr[i] = raw.charCodeAt(i);
  return arr;
}

/**
 * "Seguir" una serie = opt-in de avisos cuando sale un episodio nuevo
 * (doc 3: "¿Te avisamos cuando salga el próximo?"). Al seguir, pide permiso
 * de push web; si el usuario lo niega, igual queda el aviso in-app.
 */
export default function FollowButton({ seriesSlug }: Props) {
  const { data: session } = useSession();
  const [following, setFollowing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [showLogin, setShowLogin] = useState(false);

  useEffect(() => {
    if (!session?.user) return;
    let cancelled = false;
    fetch(`/api/follows/${encodeURIComponent(seriesSlug)}`)
      .then((r) => r.json())
      .then((d: { following?: boolean }) => {
        if (!cancelled) setFollowing(Boolean(d.following));
      })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [session?.user, seriesSlug]);

  async function subscribePush() {
    try {
      if (!VAPID_PUBLIC_KEY) return;
      if (!("serviceWorker" in navigator) || !("PushManager" in window)) return;

      const registration = await navigator.serviceWorker.register("/sw.js");
      const permission = await Notification.requestPermission();
      if (permission !== "granted") return;

      const subscription =
        (await registration.pushManager.getSubscription()) ??
        (await registration.pushManager.subscribe({
          userVisibleOnly: true,
          applicationServerKey: urlBase64ToUint8Array(VAPID_PUBLIC_KEY) as BufferSource,
        }));

      await fetch("/api/push", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(subscription.toJSON()),
      });
    } catch {
      // El push es un extra — seguir la serie nunca falla por esto
    }
  }

  async function handleToggle() {
    if (!session?.user) {
      setShowLogin(true);
      return;
    }
    if (busy) return;
    setBusy(true);
    try {
      const res = await fetch(`/api/follows/${encodeURIComponent(seriesSlug)}`, {
        method: "POST",
      });
      const d = (await res.json()) as { following?: boolean };
      const next = Boolean(d.following);
      setFollowing(next);
      if (next) await subscribePush();
    } catch {
      // estado queda como estaba
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <button
        onClick={handleToggle}
        disabled={busy}
        title={
          following
            ? "Dejar de seguir"
            : "Te avisamos cuando salga el próximo episodio"
        }
        className={`inline-flex h-10 items-center gap-2 rounded-btn border px-4 text-sm font-semibold transition-all duration-fast active:scale-[0.97] ${
          following
            ? "border-[var(--accent-border)] bg-[var(--accent-muted)] text-brand-bright"
            : "border-line-2 bg-abyss-3 text-ink-1 hover:brightness-110"
        }`}
      >
        <svg
          width="16"
          height="16"
          viewBox="0 0 24 24"
          fill={following ? "currentColor" : "none"}
          stroke="currentColor"
          strokeWidth="2"
          strokeLinecap="round"
          strokeLinejoin="round"
          aria-hidden
        >
          <path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9" />
          <path d="M10.3 21a1.94 1.94 0 0 0 3.4 0" />
        </svg>
        {following ? "Siguiendo" : "Seguir"}
      </button>
      {showLogin && <LoginModal onClose={() => setShowLogin(false)} />}
    </>
  );
}
