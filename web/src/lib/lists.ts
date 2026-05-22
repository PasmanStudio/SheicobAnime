export interface ListSummary {
  id: string;
  name: string;
  description: string | null;
  is_public: boolean;
  item_count: number;
  preview_covers: string[];
  views: number;
  created_at: string;
  updated_at: string;
}

export interface ListDetail extends Omit<ListSummary, "item_count" | "preview_covers"> {
  user_id: string;
  items: ListItem[];
}

export interface ListItem {
  series_slug: string;
  series_title: string;
  cover_url: string | null;
  added_at: string;
}
