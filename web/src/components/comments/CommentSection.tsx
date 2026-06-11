"use client";

import { useEffect, useRef, useState, lazy, Suspense } from "react";

const DisqusEmbed = lazy(() => import("./DisqusEmbed"));
const Remark42Embed = lazy(() => import("./Remark42Embed"));

type CommentProvider = "disqus" | "remark42" | "native";

export interface CommentSectionProps {
  /** Unique page identifier (e.g. episode ID) */
  pageId: string;
  /** Canonical URL of the current page */
  pageUrl: string;
}

function LoadingSkeleton() {
  return (
    <div className="space-y-3 animate-pulse">
      <div className="h-4 bg-abyss-3 rounded w-1/3" />
      <div className="h-20 bg-abyss-3 rounded" />
      <div className="h-4 bg-abyss-3 rounded w-2/3" />
      <div className="h-4 bg-abyss-3 rounded w-1/2" />
    </div>
  );
}

/**
 * SCALING CONTRACT #4
 * COMMENT_PROVIDER env var is the ONLY switch for the comment system.
 * All comment embeds go through this component — never inline Disqus/Remark42 scripts.
 */
export default function CommentSection({ pageId, pageUrl }: CommentSectionProps) {
  const provider: CommentProvider =
    (process.env.NEXT_PUBLIC_COMMENT_PROVIDER as CommentProvider) ?? "disqus";

  const [isVisible, setIsVisible] = useState(false);
  const sentinelRef = useRef<HTMLDivElement>(null);

  // IntersectionObserver lazy-load: load comments when user scrolls within 200px
  useEffect(() => {
    const el = sentinelRef.current;
    if (!el) return;

    const observer = new IntersectionObserver(
      ([entry]) => {
        if (entry.isIntersecting) {
          setIsVisible(true);
          observer.disconnect();
        }
      },
      { rootMargin: "200px" }
    );

    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  if (provider === "native") {
    return (
      <p className="text-ink-3 text-sm">
        Native comments coming soon.
      </p>
    );
  }

  return (
    <div ref={sentinelRef} className="min-h-[100px]">
      {isVisible ? (
        <Suspense fallback={<LoadingSkeleton />}>
          {provider === "disqus" && (
            <DisqusEmbed pageId={pageId} pageUrl={pageUrl} />
          )}
          {provider === "remark42" && (
            <Remark42Embed pageId={pageId} pageUrl={pageUrl} />
          )}
        </Suspense>
      ) : (
        <LoadingSkeleton />
      )}
    </div>
  );
}
