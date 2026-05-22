"use client";

import { useEffect } from "react";
import { trackListView } from "./actions";

/** Fires once on mount to count a public list view. */
export default function ListViewTracker({ listId }: { listId: string }) {
  useEffect(() => {
    trackListView(listId);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  return null;
}
