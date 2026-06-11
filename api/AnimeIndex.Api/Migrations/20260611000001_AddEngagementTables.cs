using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using AnimeIndex.Api.Data;

#nullable disable

namespace AnimeIndex.Api.Migrations
{
    /// <summary>
    /// Sistema de engagement (doc 3): XP + niveles + rachas + badges +
    /// notificaciones + encuestas de temporada. Raw SQL idempotente — el
    /// mismo script vive en db/engagement-schema.sql para correr a mano
    /// en Supabase. Las tablas user_* siguen la convención del repo:
    /// user_id TEXT sin FK a users (cross-schema con Auth.js).
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260611000001_AddEngagementTables")]
    public partial class AddEngagementTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
-- ─── Columnas de gamificación en users ─────────────────────────────
ALTER TABLE users
    ADD COLUMN IF NOT EXISTS xp_total            INT  NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS level               INT  NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS streak_days         INT  NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS streak_best         INT  NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS streak_last_day     DATE,
    ADD COLUMN IF NOT EXISTS streak_grace_used_on DATE,
    ADD COLUMN IF NOT EXISTS timezone            TEXT NOT NULL DEFAULT 'America/Argentina/Buenos_Aires',
    ADD COLUMN IF NOT EXISTS email_digest_opt_in BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS email_unsub_token   TEXT;

-- ─── Catálogo de acciones XP ────────────────────────────────────────
CREATE TABLE IF NOT EXISTS xp_actions (
    action     TEXT PRIMARY KEY,
    xp         INT  NOT NULL,
    daily_cap  INT,
    note       TEXT
);

INSERT INTO xp_actions (action, xp, daily_cap, note) VALUES
    ('episode_watched',       10, 120, 'Mirar un episodio (>= 60% reproducido — valida el caller)'),
    ('series_completed',     100, NULL, 'Completar una serie (todos los episodios >= 60%)'),
    ('comment_posted',         5,  25, 'Comentar (>= 20 caracteres — valida el caller)'),
    ('comment_like_received',  2,  20, 'Recibir like en comentario (cuentas < 7 dias no cuentan — valida el caller)'),
    ('series_liked',           1,   5, 'Dar like a una serie'),
    ('tierlist_published',    25,  25, 'Publicar tier list (>= 8 animes; cap = 1/dia)'),
    ('poll_voted',            10, NULL, 'Votar en encuesta de temporada (1 voto por encuesta via UNIQUE)'),
    ('streak_bonus',           5, NULL, 'Bonus de racha: +5 x dias consecutivos, cap +50')
ON CONFLICT (action) DO NOTHING;

-- ─── Ledger de XP ───────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS user_xp_events (
    id         BIGSERIAL PRIMARY KEY,
    user_id    TEXT NOT NULL,
    action     TEXT NOT NULL REFERENCES xp_actions(action),
    xp         INT  NOT NULL,
    ref_id     TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_uxe_user_created ON user_xp_events (user_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_uxe_user_action_day ON user_xp_events (user_id, action, created_at);
CREATE UNIQUE INDEX IF NOT EXISTS uq_uxe_user_action_ref
    ON user_xp_events (user_id, action, ref_id)
    WHERE ref_id IS NOT NULL;

-- ─── Funciones de nivel ─────────────────────────────────────────────
CREATE OR REPLACE FUNCTION xp_for_level(n INT) RETURNS INT
LANGUAGE sql IMMUTABLE AS $fn$
    SELECT (250 * n * (n - 1) / 2)::int;
$fn$;

CREATE OR REPLACE FUNCTION level_for_xp(xp INT) RETURNS INT
LANGUAGE sql IMMUTABLE AS $fn$
    SELECT GREATEST(1, floor((1 + sqrt(1 + 8.0 * xp / 250.0)) / 2)::int);
$fn$;

-- ─── award_xp: caps diarios + dedup + nivel ─────────────────────────
CREATE OR REPLACE FUNCTION award_xp(
    p_user_id TEXT,
    p_action  TEXT,
    p_ref_id  TEXT DEFAULT NULL,
    p_xp_override INT DEFAULT NULL
) RETURNS INT
LANGUAGE plpgsql AS $fn$
DECLARE
    v_xp        INT;
    v_cap       INT;
    v_today_xp  INT;
    v_award     INT;
    v_new_total INT;
BEGIN
    SELECT xp, daily_cap INTO v_xp, v_cap FROM xp_actions WHERE action = p_action;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'xp action desconocida: %', p_action;
    END IF;

    v_award := COALESCE(p_xp_override, v_xp);

    IF v_cap IS NOT NULL THEN
        SELECT COALESCE(SUM(xp), 0) INTO v_today_xp
        FROM user_xp_events
        WHERE user_id = p_user_id
          AND action = p_action
          AND created_at >= date_trunc('day', now());
        IF v_today_xp >= v_cap THEN
            RETURN 0;
        END IF;
        v_award := LEAST(v_award, v_cap - v_today_xp);
    END IF;

    BEGIN
        INSERT INTO user_xp_events (user_id, action, xp, ref_id)
        VALUES (p_user_id, p_action, v_award, p_ref_id);
    EXCEPTION WHEN unique_violation THEN
        RETURN 0;
    END;

    UPDATE users
    SET xp_total = xp_total + v_award,
        level = level_for_xp(xp_total + v_award)
    WHERE id::text = p_user_id
    RETURNING xp_total INTO v_new_total;

    RETURN v_award;
END;
$fn$;

-- ─── register_streak_day: racha con dia de gracia (1/semana) ───────
CREATE OR REPLACE FUNCTION register_streak_day(
    p_user_id TEXT,
    p_day     DATE DEFAULT NULL
) RETURNS TABLE (streak INT, bonus_xp INT, grace_used BOOLEAN)
LANGUAGE plpgsql AS $fn$
DECLARE
    v_day        DATE;
    v_last       DATE;
    v_streak     INT;
    v_grace_on   DATE;
    v_gap        INT;
    v_bonus      INT := 0;
    v_grace_used BOOLEAN := FALSE;
BEGIN
    SELECT COALESCE(p_day, (now() AT TIME ZONE u.timezone)::date),
           u.streak_last_day, u.streak_days, u.streak_grace_used_on
    INTO v_day, v_last, v_streak, v_grace_on
    FROM users u WHERE u.id::text = p_user_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'usuario inexistente: %', p_user_id;
    END IF;

    IF v_last = v_day THEN
        RETURN QUERY SELECT v_streak, 0, FALSE;
        RETURN;
    END IF;

    v_gap := COALESCE(v_day - v_last, 999);

    IF v_gap = 1 THEN
        v_streak := v_streak + 1;
    ELSIF v_gap = 2 AND (v_grace_on IS NULL OR v_grace_on < v_day - 7) THEN
        v_streak := v_streak + 1;
        v_grace_used := TRUE;
    ELSE
        v_streak := 1;
    END IF;

    v_bonus := LEAST(v_streak * 5, 50);
    PERFORM award_xp(p_user_id, 'streak_bonus', 'streak:' || v_day::text, v_bonus);

    UPDATE users
    SET streak_days = v_streak,
        streak_best = GREATEST(streak_best, v_streak),
        streak_last_day = v_day,
        streak_grace_used_on = CASE WHEN v_grace_used THEN v_day ELSE streak_grace_used_on END
    WHERE id::text = p_user_id;

    RETURN QUERY SELECT v_streak, v_bonus, v_grace_used;
END;
$fn$;

-- ─── Badges ─────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS badges (
    code        TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    description TEXT NOT NULL,
    group_name  TEXT NOT NULL,
    sort        INT  NOT NULL DEFAULT 0
);

INSERT INTO badges (code, name, description, group_name, sort) VALUES
    ('primer_paso',    'Primer paso',    'Primer episodio visto',                       'Visionado',      1),
    ('maratonista',    'Maratonista',    '12 episodios en un dia',                      'Visionado',      2),
    ('completista',    'Completista',    '10 series completadas',                       'Visionado',      3),
    ('devorador',      'Devorador',      '500 episodios vistos',                        'Visionado',      4),
    ('madrugador',     'Madrugador',     'Mirar un estreno en su primera hora',         'Visionado',      5),
    ('racha_fuego',    'Racha de fuego', 'Racha de 7 dias',                             'Constancia',     6),
    ('imparable',      'Imparable',      'Racha de 30 dias',                            'Constancia',     7),
    ('leyenda_viva',   'Leyenda viva',   'Racha de 100 dias',                           'Constancia',     8),
    ('primera_palabra','Primera palabra','Primer comentario',                           'Comunidad',      9),
    ('top_fan',        'Top fan',        '50 likes recibidos en comentarios',           'Comunidad',     10),
    ('critico',        'Critico',        'Primera tier list publicada',                 'Comunidad',     11),
    ('influyente',     'Influyente',     'Tier list con 100 likes',                     'Comunidad',     12),
    ('votante',        'Votante',        'Votar en una encuesta de temporada',          'Eventos',       13),
    ('fundador',       'Fundador',       'Cuenta creada en el primer anio del sitio',   'Especial',      14),
    ('mecenas',        'Mecenas',        'Donacion via Ko-fi',                          'Especial',      15),
    ('explorador',     'Explorador',     'Ver series de 10 generos distintos',          'Descubrimiento',16)
ON CONFLICT (code) DO NOTHING;

CREATE TABLE IF NOT EXISTS user_badges (
    user_id    TEXT NOT NULL,
    badge_code TEXT NOT NULL REFERENCES badges(code) ON DELETE CASCADE,
    earned_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    notified   BOOLEAN NOT NULL DEFAULT FALSE,
    PRIMARY KEY (user_id, badge_code)
);

CREATE INDEX IF NOT EXISTS idx_user_badges_user ON user_badges (user_id, earned_at DESC);

-- ─── Seguir series + notificaciones + push ─────────────────────────
CREATE TABLE IF NOT EXISTS series_follows (
    user_id     TEXT NOT NULL,
    series_slug TEXT NOT NULL,
    notify      BOOLEAN NOT NULL DEFAULT TRUE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, series_slug)
);

CREATE INDEX IF NOT EXISTS idx_series_follows_slug ON series_follows (series_slug) WHERE notify;

CREATE TABLE IF NOT EXISTS user_notifications (
    id         BIGSERIAL PRIMARY KEY,
    user_id    TEXT NOT NULL,
    type       TEXT NOT NULL CHECK (type IN
                 ('new_episode','comment_reply','xp_level_up','badge_earned','mention','poll','system')),
    title      TEXT NOT NULL,
    body       TEXT,
    url        TEXT,
    read_at    TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_un_user_unread ON user_notifications (user_id, created_at DESC) WHERE read_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_un_user_created ON user_notifications (user_id, created_at DESC);

CREATE TABLE IF NOT EXISTS push_subscriptions (
    id         BIGSERIAL PRIMARY KEY,
    user_id    TEXT NOT NULL,
    endpoint   TEXT NOT NULL UNIQUE,
    p256dh     TEXT NOT NULL,
    auth       TEXT NOT NULL,
    user_agent TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_push_subs_user ON push_subscriptions (user_id);

-- ─── Encuestas de temporada ─────────────────────────────────────────
CREATE TABLE IF NOT EXISTS season_polls (
    id         BIGSERIAL PRIMARY KEY,
    slug       TEXT NOT NULL UNIQUE,
    title      TEXT NOT NULL,
    starts_at  TIMESTAMPTZ NOT NULL,
    ends_at    TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS season_poll_options (
    id           BIGSERIAL PRIMARY KEY,
    poll_id      BIGINT NOT NULL REFERENCES season_polls(id) ON DELETE CASCADE,
    series_slug  TEXT NOT NULL,
    series_title TEXT NOT NULL,
    cover_url    TEXT,
    sort         INT NOT NULL DEFAULT 0,
    UNIQUE (poll_id, series_slug)
);

CREATE TABLE IF NOT EXISTS season_poll_votes (
    poll_id    BIGINT NOT NULL REFERENCES season_polls(id) ON DELETE CASCADE,
    user_id    TEXT NOT NULL,
    option_id  BIGINT NOT NULL REFERENCES season_poll_options(id) ON DELETE CASCADE,
    weight     INT NOT NULL DEFAULT 1 CHECK (weight IN (1, 2)),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (poll_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_spv_option ON season_poll_votes (option_id);

-- ─── Verificación batch de badges (job nocturno) ────────────────────
CREATE OR REPLACE FUNCTION grant_badges_batch() RETURNS INT
LANGUAGE plpgsql AS $fn$
DECLARE
    v_granted INT := 0;
    v_count INT;
BEGIN
    INSERT INTO user_badges (user_id, badge_code)
    SELECT DISTINCT user_id, 'primer_paso' FROM user_episode_history
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_count = ROW_COUNT; v_granted := v_granted + v_count;

    INSERT INTO user_badges (user_id, badge_code)
    SELECT user_id, 'maratonista' FROM (
        SELECT user_id, watched_at::date AS d, COUNT(*) AS c
        FROM user_episode_history GROUP BY user_id, watched_at::date
    ) t WHERE c >= 12
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_count = ROW_COUNT; v_granted := v_granted + v_count;

    INSERT INTO user_badges (user_id, badge_code)
    SELECT user_id, 'devorador' FROM (
        SELECT user_id, COUNT(*) AS c FROM user_episode_history GROUP BY user_id
    ) t WHERE c >= 500
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_count = ROW_COUNT; v_granted := v_granted + v_count;

    INSERT INTO user_badges (user_id, badge_code)
    SELECT user_id, 'completista' FROM (
        SELECT user_id, COUNT(*) AS c FROM user_watch_entries
        WHERE status = 'visto' GROUP BY user_id
    ) t WHERE c >= 10
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_count = ROW_COUNT; v_granted := v_granted + v_count;

    INSERT INTO user_badges (user_id, badge_code)
    SELECT id::text, 'racha_fuego' FROM users WHERE streak_best >= 7
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_count = ROW_COUNT; v_granted := v_granted + v_count;

    INSERT INTO user_badges (user_id, badge_code)
    SELECT id::text, 'imparable' FROM users WHERE streak_best >= 30
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_count = ROW_COUNT; v_granted := v_granted + v_count;

    INSERT INTO user_badges (user_id, badge_code)
    SELECT id::text, 'leyenda_viva' FROM users WHERE streak_best >= 100
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_count = ROW_COUNT; v_granted := v_granted + v_count;

    INSERT INTO user_badges (user_id, badge_code)
    SELECT DISTINCT user_id, 'critico' FROM user_tier_lists WHERE is_public
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_count = ROW_COUNT; v_granted := v_granted + v_count;

    INSERT INTO user_badges (user_id, badge_code)
    SELECT DISTINCT user_id, 'votante' FROM season_poll_votes
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_count = ROW_COUNT; v_granted := v_granted + v_count;

    INSERT INTO user_badges (user_id, badge_code)
    SELECT user_id, 'explorador' FROM (
        SELECT h.user_id, COUNT(DISTINCT sg.genre_id) AS c
        FROM (SELECT DISTINCT user_id, series_slug FROM user_episode_history) h
        JOIN series s ON s.slug = h.series_slug
        JOIN series_genres sg ON sg.series_id = s.id
        GROUP BY h.user_id
    ) t WHERE c >= 10
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_count = ROW_COUNT; v_granted := v_granted + v_count;

    INSERT INTO user_badges (user_id, badge_code)
    SELECT id::text, 'fundador' FROM users WHERE created_at < TIMESTAMPTZ '2027-06-01 00:00:00+00'
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_count = ROW_COUNT; v_granted := v_granted + v_count;

    RETURN v_granted;
END;
$fn$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP FUNCTION IF EXISTS grant_badges_batch();
DROP FUNCTION IF EXISTS register_streak_day(TEXT, DATE);
DROP FUNCTION IF EXISTS award_xp(TEXT, TEXT, TEXT, INT);
DROP FUNCTION IF EXISTS level_for_xp(INT);
DROP FUNCTION IF EXISTS xp_for_level(INT);
DROP TABLE IF EXISTS season_poll_votes;
DROP TABLE IF EXISTS season_poll_options;
DROP TABLE IF EXISTS season_polls;
DROP TABLE IF EXISTS push_subscriptions;
DROP TABLE IF EXISTS user_notifications;
DROP TABLE IF EXISTS series_follows;
DROP TABLE IF EXISTS user_badges;
DROP TABLE IF EXISTS badges;
DROP TABLE IF EXISTS user_xp_events;
DROP TABLE IF EXISTS xp_actions;
ALTER TABLE users
    DROP COLUMN IF EXISTS xp_total,
    DROP COLUMN IF EXISTS level,
    DROP COLUMN IF EXISTS streak_days,
    DROP COLUMN IF EXISTS streak_best,
    DROP COLUMN IF EXISTS streak_last_day,
    DROP COLUMN IF EXISTS streak_grace_used_on,
    DROP COLUMN IF EXISTS timezone,
    DROP COLUMN IF EXISTS email_digest_opt_in,
    DROP COLUMN IF EXISTS email_unsub_token;
");
        }
    }
}
