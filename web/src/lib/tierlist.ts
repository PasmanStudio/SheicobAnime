export const TIERS = ['S', 'A', 'B', 'C', 'D', 'F'] as const;
export type Tier = typeof TIERS[number];

export const TIER_COLORS: Record<Tier, { bg: string; border: string; text: string; label: string }> = {
  S: { bg: 'bg-red-600',     border: 'border-red-500',    text: 'text-white',          label: 'Obra maestra' },
  A: { bg: 'bg-orange-500',  border: 'border-orange-400', text: 'text-white',          label: 'Excelente' },
  B: { bg: 'bg-yellow-500',  border: 'border-yellow-400', text: 'text-neutral-900',    label: 'Bueno' },
  C: { bg: 'bg-green-600',   border: 'border-green-500',  text: 'text-white',          label: 'Promedio' },
  D: { bg: 'bg-blue-600',    border: 'border-blue-500',   text: 'text-white',          label: 'Por debajo' },
  F: { bg: 'bg-neutral-600', border: 'border-neutral-500',text: 'text-neutral-200',    label: 'Terrible' },
};

export interface TierListSummary {
  id: string;
  name: string;
  is_public: boolean;
  entry_count: number;
  created_at: string;
  updated_at: string;
}

export interface TierEntry {
  tier_list_id: string;
  series_slug: string;
  series_title: string;
  cover_url: string | null;
  tier: Tier;
  position: number;
  added_at: string;
}

export interface TierListDetail {
  id: string;
  name: string;
  is_public: boolean;
  user_id: string;
  created_at: string;
  updated_at: string;
  entries: TierEntry[];
}
