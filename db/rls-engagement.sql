-- ════════════════════════════════════════════════════════════════════
-- RLS para las tablas de engagement (doc 3) — MISMA postura que el resto
-- del esquema (ver db/rls-policies.sql): RLS habilitado con default-deny,
-- SIN políticas para anon/authenticated.
--
-- Por qué: el frontend y el scraper acceden con una conexión Postgres
-- privilegiada (service_role / postgres) que BYPASSEA RLS. El anon key de
-- Supabase NUNCA se usa. Estas tablas se crearon por SQL crudo sin RLS, así
-- que quedaban potencialmente expuestas vía la API REST anónima de Supabase.
-- Esto las cierra. Correr una vez en el SQL editor de Supabase.
--
-- Idempotente: ENABLE ROW LEVEL SECURITY no falla si ya está habilitado.
-- ════════════════════════════════════════════════════════════════════

ALTER TABLE public.xp_actions            ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.user_xp_events        ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.badges                ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.user_badges           ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.series_follows        ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.user_notifications    ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.push_subscriptions    ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.season_polls          ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.season_poll_options   ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.season_poll_votes     ENABLE ROW LEVEL SECURITY;

-- Defensa en profundidad: revocar cualquier grant heredado al anon/authenticated.
REVOKE ALL ON public.xp_actions, public.user_xp_events, public.badges,
              public.user_badges, public.series_follows, public.user_notifications,
              public.push_subscriptions, public.season_polls, public.season_poll_options,
              public.season_poll_votes
  FROM anon, authenticated;

-- Verificar (todas deben aparecer con rowsecurity = true):
-- SELECT relname, relrowsecurity FROM pg_class
--   WHERE relname IN ('push_subscriptions','user_notifications','season_poll_votes',
--     'user_xp_events','user_badges','series_follows','season_polls',
--     'season_poll_options','badges','xp_actions');
