-- ════════════════════════════════════════════════════════════════════
-- Crear una encuesta de temporada (doc 3 — eventos comunitarios).
-- Correr a mano en Supabase cuando arranca una temporada nueva.
--
-- La encuesta aparece automáticamente en /encuestas y en el banner del home
-- mientras now() esté entre starts_at y ends_at. Editá las fechas, el título
-- y la lista de opciones (los slugs/títulos/covers salen de la tabla series).
-- ════════════════════════════════════════════════════════════════════

BEGIN;

-- 1. La encuesta. 'slug' es único — cambialo por temporada.
INSERT INTO season_polls (slug, title, starts_at, ends_at)
VALUES (
  'verano-2026',
  'Votá el mejor estreno de Verano 2026',
  TIMESTAMPTZ '2026-07-01 00:00:00-03',  -- inicio (hora Argentina)
  TIMESTAMPTZ '2026-07-31 23:59:59-03'   -- fin
)
ON CONFLICT (slug) DO NOTHING;

-- 2. Las opciones. Tomamos cover/título reales de la tabla series por slug.
--    Reemplazá la lista de slugs por los estrenos de la temporada.
INSERT INTO season_poll_options (poll_id, series_slug, series_title, cover_url, sort)
SELECT p.id, s.slug, s.title, s.cover_url, row_number() OVER (ORDER BY s.title)
FROM season_polls p
JOIN series s ON s.slug = ANY(ARRAY[
  'frieren',
  'kaiju-no-8',
  'dan-da-dan',
  'solo-leveling'
  -- ... agregá los slugs reales de la temporada
])
WHERE p.slug = 'verano-2026'
ON CONFLICT (poll_id, series_slug) DO NOTHING;

COMMIT;

-- Verificar:
-- SELECT o.series_title FROM season_poll_options o
--   JOIN season_polls p ON p.id = o.poll_id WHERE p.slug = 'verano-2026';
