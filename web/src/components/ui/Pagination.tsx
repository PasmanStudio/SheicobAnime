import Link from "next/link";

interface PaginationProps {
  page: number;
  total: number;
  pageSize: number;
  /** Base path including any existing query params except `page`, e.g. "/search?q=naruto" */
  basePath: string;
}

export default function Pagination({
  page,
  total,
  pageSize,
  basePath,
}: PaginationProps) {
  const totalPages = Math.ceil(total / pageSize);
  if (totalPages <= 1) return null;

  const sep = basePath.includes("?") ? "&" : "?";
  const pageUrl = (p: number) => `${basePath}${sep}page=${p}`;

  return (
    <nav
      className="flex items-center justify-center gap-3 mt-8"
      aria-label="Pagination"
    >
      {page > 1 ? (
        <Link
          href={pageUrl(page - 1)}
          className="px-4 py-2 rounded-md bg-neutral-800 hover:bg-neutral-700 text-sm text-white transition-colors"
        >
          ← Previous
        </Link>
      ) : (
        <span className="px-4 py-2 rounded-md bg-neutral-800/50 text-sm text-neutral-600 cursor-not-allowed">
          ← Previous
        </span>
      )}

      <span className="text-sm text-neutral-400">
        Page {page} of {totalPages}
      </span>

      {page < totalPages ? (
        <Link
          href={pageUrl(page + 1)}
          className="px-4 py-2 rounded-md bg-neutral-800 hover:bg-neutral-700 text-sm text-white transition-colors"
        >
          Next →
        </Link>
      ) : (
        <span className="px-4 py-2 rounded-md bg-neutral-800/50 text-sm text-neutral-600 cursor-not-allowed">
          Next →
        </span>
      )}
    </nav>
  );
}
