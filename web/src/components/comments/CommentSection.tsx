"use client";

export interface CommentSectionProps {
  /** Unique page identifier (e.g. episode ID) */
  pageId: string;
  /** Canonical URL of the current page */
  pageUrl: string;
}

/**
 * Phase-6 stub — renders nothing until the comment provider is wired.
 * COMMENT_PROVIDER env var is the only switch between disqus / remark42 / native.
 */
// eslint-disable-next-line @typescript-eslint/no-unused-vars
export default function CommentSection(_props: CommentSectionProps) {
  return null;
}
