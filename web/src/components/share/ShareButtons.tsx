"use client";

import { useState } from "react";

interface Props {
  /** Full page URL to share (already encoded as short ID) */
  url: string;
  /** Text shown on share cards / message prefixes */
  text: string;
  /** Optional label shown before the buttons. Defaults to "Compartir:" */
  label?: string;
}

/**
 * Reusable share row: WhatsApp · Telegram · X · copy link.
 * Pass the full URL — this component does NOT build it.
 */
export default function ShareButtons({ url, text, label = "Compartir:" }: Props) {
  const [copied, setCopied] = useState(false);

  const encodedText = encodeURIComponent(`${text}\n${url}`);
  const encodedUrl  = encodeURIComponent(url);

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(url);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      // Fallback — silently fail (user can copy from address bar)
    }
  };

  return (
    <div className="flex items-center gap-2 flex-wrap">
      {label && <span className="text-xs text-ink-3 shrink-0">{label}</span>}

      {/* WhatsApp */}
      <a
        href={`https://wa.me/?text=${encodedText}`}
        target="_blank"
        rel="noopener noreferrer"
        title="Compartir en WhatsApp"
        className="flex items-center gap-1.5 px-2.5 py-1 rounded-lg bg-success-soft hover:bg-success-line text-success text-xs font-medium transition-colors"
      >
        <svg className="w-3.5 h-3.5" viewBox="0 0 24 24" fill="currentColor">
          <path d="M17.472 14.382c-.297-.149-1.758-.867-2.03-.967-.273-.099-.471-.148-.67.15-.197.297-.767.966-.94 1.164-.173.199-.347.223-.644.075-.297-.15-1.255-.463-2.39-1.475-.883-.788-1.48-1.761-1.653-2.059-.173-.297-.018-.458.13-.606.134-.133.298-.347.446-.52.149-.174.198-.298.298-.497.099-.198.05-.371-.025-.52-.075-.149-.669-1.612-.916-2.207-.242-.579-.487-.5-.669-.51-.173-.008-.371-.01-.57-.01-.198 0-.52.074-.792.372-.272.297-1.04 1.016-1.04 2.479 0 1.462 1.065 2.875 1.213 3.074.149.198 2.096 3.2 5.077 4.487.709.306 1.262.489 1.694.625.712.227 1.36.195 1.871.118.571-.085 1.758-.719 2.006-1.413.248-.694.248-1.289.173-1.413-.074-.124-.272-.198-.57-.347z" />
          <path d="M12 2C6.477 2 2 6.477 2 12c0 1.89.525 3.66 1.438 5.168L2 22l4.918-1.41A9.955 9.955 0 0012 22c5.523 0 10-4.477 10-10S17.523 2 12 2zm0 18a7.955 7.955 0 01-4.055-1.11l-.29-.174-3.005.862.846-2.925-.19-.3A7.944 7.944 0 014 12c0-4.411 3.589-8 8-8s8 3.589 8 8-3.589 8-8 8z" />
        </svg>
        WhatsApp
      </a>

      {/* Telegram */}
      <a
        href={`https://t.me/share/url?url=${encodedUrl}&text=${encodeURIComponent(text)}`}
        target="_blank"
        rel="noopener noreferrer"
        title="Compartir en Telegram"
        className="flex items-center gap-1.5 px-2.5 py-1 rounded-lg bg-sky-700/20 hover:bg-sky-700/40 text-sky-400 text-xs font-medium transition-colors"
      >
        <svg className="w-3.5 h-3.5" viewBox="0 0 24 24" fill="currentColor">
          <path d="M12 0C5.373 0 0 5.373 0 12s5.373 12 12 12 12-5.373 12-12S18.627 0 12 0zm5.894 8.221l-1.97 9.28c-.145.658-.537.818-1.084.508l-3-2.21-1.447 1.394c-.16.16-.295.295-.605.295l.213-3.053 5.56-5.023c.242-.213-.054-.333-.373-.12L7.548 13.99l-2.94-.918c-.64-.203-.653-.64.135-.954l11.566-4.458c.537-.194 1.006.131.585.561z" />
        </svg>
        Telegram
      </a>

      {/* X / Twitter */}
      <a
        href={`https://x.com/intent/tweet?text=${encodedText}`}
        target="_blank"
        rel="noopener noreferrer"
        title="Compartir en X"
        className="flex items-center gap-1.5 px-2.5 py-1 rounded-lg bg-abyss-3/40 hover:bg-abyss-3/70 text-ink-2 text-xs font-medium transition-colors"
      >
        <svg className="w-3.5 h-3.5" viewBox="0 0 24 24" fill="currentColor">
          <path d="M18.244 2.25h3.308l-7.227 8.26 8.502 11.24H16.17l-4.714-6.231-5.401 6.231H2.746l7.73-8.835L1.254 2.25H8.08l4.259 5.63zm-1.161 17.52h1.833L7.084 4.126H5.117z" />
        </svg>
        X
      </a>

      {/* Copy link */}
      <button
        onClick={handleCopy}
        title="Copiar link"
        className="flex items-center gap-1.5 px-2.5 py-1 rounded-lg bg-abyss-3/40 hover:bg-abyss-3/70 text-ink-2 text-xs font-medium transition-colors"
      >
        {copied ? (
          <>
            <svg className="w-3.5 h-3.5 text-success" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.5}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
            </svg>
            <span className="text-success">Copiado</span>
          </>
        ) : (
          <>
            <svg className="w-3.5 h-3.5" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
              <rect x="9" y="9" width="13" height="13" rx="2" ry="2" />
              <path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" />
            </svg>
            Copiar link
          </>
        )}
      </button>
    </div>
  );
}
