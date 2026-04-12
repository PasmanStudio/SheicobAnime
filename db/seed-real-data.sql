-- ============================================================================
-- SheicobAnime — Real data seed for local testing
-- Run: psql -h localhost -U animeindex -d animeindex -f db/seed-real-data.sql
-- Or via Adminer at http://localhost:8080
--
-- This inserts real anime series with YouTube trailer embeds as mirrors
-- so you can verify the full end-to-end flow: browse → episode → player.
-- ============================================================================

BEGIN;

-- ─── Clean previous test data ──────────────────────────────────────────────
-- Only deletes series that match our seed slugs, preserving any other data
DELETE FROM mirrors WHERE episode_id IN (
    SELECT e.id FROM episodes e
    JOIN series s ON e.series_id = s.id
    WHERE s.slug IN (
        'attack-on-titan',
        'demon-slayer',
        'jujutsu-kaisen',
        'one-punch-man',
        'spy-x-family',
        'chainsaw-man',
        'mob-psycho-100',
        'vinland-saga'
    )
);
DELETE FROM episodes WHERE series_id IN (
    SELECT id FROM series WHERE slug IN (
        'attack-on-titan',
        'demon-slayer',
        'jujutsu-kaisen',
        'one-punch-man',
        'spy-x-family',
        'chainsaw-man',
        'mob-psycho-100',
        'vinland-saga'
    )
);
DELETE FROM series_genres WHERE series_id IN (
    SELECT id FROM series WHERE slug IN (
        'attack-on-titan',
        'demon-slayer',
        'jujutsu-kaisen',
        'one-punch-man',
        'spy-x-family',
        'chainsaw-man',
        'mob-psycho-100',
        'vinland-saga'
    )
);
DELETE FROM series WHERE slug IN (
    'attack-on-titan',
    'demon-slayer',
    'jujutsu-kaisen',
    'one-punch-man',
    'spy-x-family',
    'chainsaw-man',
    'mob-psycho-100',
    'vinland-saga'
);

-- ─── Series ────────────────────────────────────────────────────────────────
INSERT INTO series (id, slug, title, title_romaji, title_native, synopsis, cover_url, banner_url, year, status, type, score, episode_count, metadata)
VALUES
  ('a0000001-0000-0000-0000-000000000001', 'attack-on-titan',
   'Attack on Titan', 'Shingeki no Kyojin', '進撃の巨人',
   'Humanity lives inside cities surrounded by enormous walls due to the Titans, gigantic humanoid beings. The story follows Eren Yeager, who vows to exterminate the Titans after they bring about the destruction of his hometown and the death of his mother.',
   'https://cdn.myanimelist.net/images/anime/10/47347.jpg',
   NULL,
   2013, 'completed', 'tv', 9.00, 25, '{}'),

  ('a0000002-0000-0000-0000-000000000002', 'demon-slayer',
   'Demon Slayer: Kimetsu no Yaiba', 'Kimetsu no Yaiba', '鬼滅の刃',
   'A family is attacked by demons and only two members survive - Tanjiro and his sister Nezuko, who is turning into a demon slowly. Tanjiro sets out to become a demon slayer to avenge his family and cure his sister.',
   'https://cdn.myanimelist.net/images/anime/1286/99889.jpg',
   NULL,
   2019, 'ongoing', 'tv', 8.50, 26, '{}'),

  ('a0000003-0000-0000-0000-000000000003', 'jujutsu-kaisen',
   'Jujutsu Kaisen', 'Jujutsu Kaisen', '呪術廻戦',
   'Yuji Itadori is a boy with tremendous physical strength, though he lives a completely ordinary life. One day, to save a friend who has been attacked by Curses, he eats a finger of Ryomen Sukuna, taking the Curse into his own soul.',
   'https://cdn.myanimelist.net/images/anime/1171/109222.jpg',
   NULL,
   2020, 'completed', 'tv', 8.70, 24, '{}'),

  ('a0000004-0000-0000-0000-000000000004', 'one-punch-man',
   'One Punch Man', 'One Punch Man', 'ワンパンマン',
   'Saitama is a hero who only became a hero for fun. After three years of special training, he has become so strong that he can defeat any enemy with a single punch. But having overwhelming power is actually kind of boring.',
   'https://cdn.myanimelist.net/images/anime/12/73235.jpg',
   NULL,
   2015, 'ongoing', 'tv', 8.50, 12, '{}'),

  ('a0000005-0000-0000-0000-000000000005', 'spy-x-family',
   'SPY×FAMILY', 'SPY×FAMILY', 'SPY×FAMILY',
   'A spy known as Twilight is tasked with building a family to execute a mission. He adopts a telepathic girl and marries an assassin, each hiding their true identities from each other.',
   'https://cdn.myanimelist.net/images/anime/1441/124795.jpg',
   NULL,
   2022, 'ongoing', 'tv', 8.60, 12, '{}'),

  ('a0000006-0000-0000-0000-000000000006', 'chainsaw-man',
   'Chainsaw Man', 'Chainsaw Man', 'チェンソーマン',
   'Denji is a teenage boy living with a Chainsaw Devil named Pochita. Due to the debt his father left behind, he has been forced to live in poverty. One day, Denji is betrayed and killed, but Pochita merges with his body.',
   'https://cdn.myanimelist.net/images/anime/1806/126216.jpg',
   NULL,
   2022, 'completed', 'tv', 8.30, 12, '{}'),

  ('a0000007-0000-0000-0000-000000000007', 'mob-psycho-100',
   'Mob Psycho 100', 'Mob Psycho 100', 'モブサイコ100',
   'Shigeo Kageyama is a 8th grader with psychic abilities. He could bend spoons and lift objects with his mind from a young age, but he slowly began to withhold from using his abilities in public due to the negative attention he kept receiving.',
   'https://cdn.myanimelist.net/images/anime/8/80356.jpg',
   NULL,
   2016, 'completed', 'tv', 8.50, 12, '{}'),

  ('a0000008-0000-0000-0000-000000000008', 'vinland-saga',
   'Vinland Saga', 'Vinland Saga', 'ヴィンランド・サガ',
   'Thorfinn, son of one of the Vikings greatest warriors, is among the finest fighters in the merry band of mercenaries run by the cunning Askeladd. Despite his boyish appearance, he is the deadliest in the group.',
   'https://cdn.myanimelist.net/images/anime/1500/103005.jpg',
   NULL,
   2019, 'completed', 'tv', 8.70, 24, '{}');

-- ─── Genre associations ────────────────────────────────────────────────────
-- Get genre IDs dynamically
INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000001-0000-0000-0000-000000000001'::uuid, id FROM genres WHERE name = 'Action'
ON CONFLICT DO NOTHING;
INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000001-0000-0000-0000-000000000001'::uuid, id FROM genres WHERE name = 'Drama'
ON CONFLICT DO NOTHING;
INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000001-0000-0000-0000-000000000001'::uuid, id FROM genres WHERE name = 'Fantasy'
ON CONFLICT DO NOTHING;

INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000002-0000-0000-0000-000000000002'::uuid, id FROM genres WHERE name = 'Action'
ON CONFLICT DO NOTHING;
INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000002-0000-0000-0000-000000000002'::uuid, id FROM genres WHERE name = 'Supernatural'
ON CONFLICT DO NOTHING;

INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000003-0000-0000-0000-000000000003'::uuid, id FROM genres WHERE name = 'Action'
ON CONFLICT DO NOTHING;
INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000003-0000-0000-0000-000000000003'::uuid, id FROM genres WHERE name = 'Supernatural'
ON CONFLICT DO NOTHING;

INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000004-0000-0000-0000-000000000004'::uuid, id FROM genres WHERE name = 'Action'
ON CONFLICT DO NOTHING;
INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000004-0000-0000-0000-000000000004'::uuid, id FROM genres WHERE name = 'Comedy'
ON CONFLICT DO NOTHING;

INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000005-0000-0000-0000-000000000005'::uuid, id FROM genres WHERE name = 'Action'
ON CONFLICT DO NOTHING;
INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000005-0000-0000-0000-000000000005'::uuid, id FROM genres WHERE name = 'Comedy'
ON CONFLICT DO NOTHING;

INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000006-0000-0000-0000-000000000006'::uuid, id FROM genres WHERE name = 'Action'
ON CONFLICT DO NOTHING;
INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000006-0000-0000-0000-000000000006'::uuid, id FROM genres WHERE name = 'Horror'
ON CONFLICT DO NOTHING;

INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000007-0000-0000-0000-000000000007'::uuid, id FROM genres WHERE name = 'Action'
ON CONFLICT DO NOTHING;
INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000007-0000-0000-0000-000000000007'::uuid, id FROM genres WHERE name = 'Comedy'
ON CONFLICT DO NOTHING;
INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000007-0000-0000-0000-000000000007'::uuid, id FROM genres WHERE name = 'Supernatural'
ON CONFLICT DO NOTHING;

INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000008-0000-0000-0000-000000000008'::uuid, id FROM genres WHERE name = 'Action'
ON CONFLICT DO NOTHING;
INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000008-0000-0000-0000-000000000008'::uuid, id FROM genres WHERE name = 'Adventure'
ON CONFLICT DO NOTHING;
INSERT INTO series_genres (series_id, genre_id)
SELECT 'a0000008-0000-0000-0000-000000000008'::uuid, id FROM genres WHERE name = 'Drama'
ON CONFLICT DO NOTHING;

-- ─── Episodes ──────────────────────────────────────────────────────────────
-- Attack on Titan — 5 episodes
INSERT INTO episodes (id, series_id, episode_number, title, is_published, duration_secs, aired_at) VALUES
  ('e0000001-0001-0000-0000-000000000001', 'a0000001-0000-0000-0000-000000000001', 1, 'To You, in 2000 Years: The Fall of Shiganshina, Part 1', true, 1440, '2013-04-07'),
  ('e0000001-0002-0000-0000-000000000002', 'a0000001-0000-0000-0000-000000000001', 2, 'That Day: The Fall of Shiganshina, Part 2', true, 1440, '2013-04-14'),
  ('e0000001-0003-0000-0000-000000000003', 'a0000001-0000-0000-0000-000000000001', 3, 'A Dim Light Amid Despair: Humanity''s Comeback, Part 1', true, 1440, '2013-04-21'),
  ('e0000001-0004-0000-0000-000000000004', 'a0000001-0000-0000-0000-000000000001', 4, 'The Night of the Closing Ceremony: Humanity''s Comeback, Part 2', true, 1440, '2013-04-28'),
  ('e0000001-0005-0000-0000-000000000005', 'a0000001-0000-0000-0000-000000000001', 5, 'First Battle: The Struggle for Trost, Part 1', true, 1440, '2013-05-05');

-- Demon Slayer — 4 episodes
INSERT INTO episodes (id, series_id, episode_number, title, is_published, duration_secs, aired_at) VALUES
  ('e0000002-0001-0000-0000-000000000001', 'a0000002-0000-0000-0000-000000000002', 1, 'Cruelty', true, 1440, '2019-04-06'),
  ('e0000002-0002-0000-0000-000000000002', 'a0000002-0000-0000-0000-000000000002', 2, 'Trainer Sakonji Urokodaki', true, 1440, '2019-04-13'),
  ('e0000002-0003-0000-0000-000000000003', 'a0000002-0000-0000-0000-000000000002', 3, 'Sabito and Makomo', true, 1440, '2019-04-20'),
  ('e0000002-0004-0000-0000-000000000004', 'a0000002-0000-0000-0000-000000000002', 4, 'Final Selection', true, 1440, '2019-04-27');

-- Jujutsu Kaisen — 4 episodes
INSERT INTO episodes (id, series_id, episode_number, title, is_published, duration_secs, aired_at) VALUES
  ('e0000003-0001-0000-0000-000000000001', 'a0000003-0000-0000-0000-000000000003', 1, 'Ryomen Sukuna', true, 1440, '2020-10-03'),
  ('e0000003-0002-0000-0000-000000000002', 'a0000003-0000-0000-0000-000000000003', 2, 'For Myself', true, 1440, '2020-10-10'),
  ('e0000003-0003-0000-0000-000000000003', 'a0000003-0000-0000-0000-000000000003', 3, 'Girl of Steel', true, 1440, '2020-10-17'),
  ('e0000003-0004-0000-0000-000000000004', 'a0000003-0000-0000-0000-000000000003', 4, 'Curse Womb Must Die', true, 1440, '2020-10-24');

-- One Punch Man — 3 episodes
INSERT INTO episodes (id, series_id, episode_number, title, is_published, duration_secs, aired_at) VALUES
  ('e0000004-0001-0000-0000-000000000001', 'a0000004-0000-0000-0000-000000000004', 1, 'The Strongest Man', true, 1440, '2015-10-05'),
  ('e0000004-0002-0000-0000-000000000002', 'a0000004-0000-0000-0000-000000000004', 2, 'The Lone Cyborg', true, 1440, '2015-10-12'),
  ('e0000004-0003-0000-0000-000000000003', 'a0000004-0000-0000-0000-000000000004', 3, 'The Obsessive Scientist', true, 1440, '2015-10-19');

-- SPY×FAMILY — 3 episodes
INSERT INTO episodes (id, series_id, episode_number, title, is_published, duration_secs, aired_at) VALUES
  ('e0000005-0001-0000-0000-000000000001', 'a0000005-0000-0000-0000-000000000005', 1, 'Operation Strix', true, 1440, '2022-04-09'),
  ('e0000005-0002-0000-0000-000000000002', 'a0000005-0000-0000-0000-000000000005', 2, 'Secure a Wife', true, 1440, '2022-04-16'),
  ('e0000005-0003-0000-0000-000000000003', 'a0000005-0000-0000-0000-000000000005', 3, 'Prepare for the Interview', true, 1440, '2022-04-23');

-- Chainsaw Man — 3 episodes
INSERT INTO episodes (id, series_id, episode_number, title, is_published, duration_secs, aired_at) VALUES
  ('e0000006-0001-0000-0000-000000000001', 'a0000006-0000-0000-0000-000000000006', 1, 'Dog & Chainsaw', true, 1440, '2022-10-12'),
  ('e0000006-0002-0000-0000-000000000002', 'a0000006-0000-0000-0000-000000000006', 2, 'Arrival in Tokyo', true, 1440, '2022-10-19'),
  ('e0000006-0003-0000-0000-000000000003', 'a0000006-0000-0000-0000-000000000006', 3, 'Meowy''s Whereabouts', true, 1440, '2022-10-26');

-- Mob Psycho 100 — 3 episodes
INSERT INTO episodes (id, series_id, episode_number, title, is_published, duration_secs, aired_at) VALUES
  ('e0000007-0001-0000-0000-000000000001', 'a0000007-0000-0000-0000-000000000007', 1, 'Self-Proclaimed Psychic: Reigen Arataka ~And Mob~', true, 1440, '2016-07-12'),
  ('e0000007-0002-0000-0000-000000000002', 'a0000007-0000-0000-0000-000000000007', 2, 'Doubts About Youth ~The Telepathy Club Appears~', true, 1440, '2016-07-19'),
  ('e0000007-0003-0000-0000-000000000003', 'a0000007-0000-0000-0000-000000000007', 3, 'An Invitation to a Meeting ~Simply Put, a Cult~', true, 1440, '2016-07-26');

-- Vinland Saga — 3 episodes
INSERT INTO episodes (id, series_id, episode_number, title, is_published, duration_secs, aired_at) VALUES
  ('e0000008-0001-0000-0000-000000000001', 'a0000008-0000-0000-0000-000000000008', 1, 'Somewhere Not Here', true, 1440, '2019-07-08'),
  ('e0000008-0002-0000-0000-000000000002', 'a0000008-0000-0000-0000-000000000008', 2, 'Sword', true, 1440, '2019-07-15'),
  ('e0000008-0003-0000-0000-000000000003', 'a0000008-0000-0000-0000-000000000008', 3, 'Troll', true, 1440, '2019-07-22');

-- ─── Mirrors (YouTube embeds — official trailers/clips) ────────────────────
-- These are real embeddable YouTube URLs that work in iframes

-- Attack on Titan mirrors
INSERT INTO mirrors (episode_id, provider_name, embed_url, quality_label, priority, is_active) VALUES
  ('e0000001-0001-0000-0000-000000000001', 'YouTube', 'https://www.youtube.com/embed/MGRm4IzK1SQ', 1080, 0, true),
  ('e0000001-0001-0000-0000-000000000001', 'YouTube-2', 'https://www.youtube.com/embed/LHtdKWJdif4', 720, 1, true),
  ('e0000001-0002-0000-0000-000000000002', 'YouTube', 'https://www.youtube.com/embed/LHtdKWJdif4', 1080, 0, true),
  ('e0000001-0003-0000-0000-000000000003', 'YouTube', 'https://www.youtube.com/embed/MGRm4IzK1SQ', 1080, 0, true),
  ('e0000001-0004-0000-0000-000000000004', 'YouTube', 'https://www.youtube.com/embed/LHtdKWJdif4', 720, 0, true),
  ('e0000001-0005-0000-0000-000000000005', 'YouTube', 'https://www.youtube.com/embed/MGRm4IzK1SQ', 1080, 0, true);

-- Demon Slayer mirrors
INSERT INTO mirrors (episode_id, provider_name, embed_url, quality_label, priority, is_active) VALUES
  ('e0000002-0001-0000-0000-000000000001', 'YouTube', 'https://www.youtube.com/embed/VQGCKyvzIM4', 1080, 0, true),
  ('e0000002-0001-0000-0000-000000000001', 'YouTube-2', 'https://www.youtube.com/embed/6vMuaZirITM', 720, 1, true),
  ('e0000002-0002-0000-0000-000000000002', 'YouTube', 'https://www.youtube.com/embed/VQGCKyvzIM4', 1080, 0, true),
  ('e0000002-0003-0000-0000-000000000003', 'YouTube', 'https://www.youtube.com/embed/6vMuaZirITM', 720, 0, true),
  ('e0000002-0004-0000-0000-000000000004', 'YouTube', 'https://www.youtube.com/embed/VQGCKyvzIM4', 1080, 0, true);

-- Jujutsu Kaisen mirrors
INSERT INTO mirrors (episode_id, provider_name, embed_url, quality_label, priority, is_active) VALUES
  ('e0000003-0001-0000-0000-000000000001', 'YouTube', 'https://www.youtube.com/embed/4A_X-Dvl0ws', 1080, 0, true),
  ('e0000003-0001-0000-0000-000000000001', 'YouTube-2', 'https://www.youtube.com/embed/pkKu9hLT-t8', 720, 1, true),
  ('e0000003-0002-0000-0000-000000000002', 'YouTube', 'https://www.youtube.com/embed/4A_X-Dvl0ws', 1080, 0, true),
  ('e0000003-0003-0000-0000-000000000003', 'YouTube', 'https://www.youtube.com/embed/pkKu9hLT-t8', 720, 0, true),
  ('e0000003-0004-0000-0000-000000000004', 'YouTube', 'https://www.youtube.com/embed/4A_X-Dvl0ws', 1080, 0, true);

-- One Punch Man mirrors
INSERT INTO mirrors (episode_id, provider_name, embed_url, quality_label, priority, is_active) VALUES
  ('e0000004-0001-0000-0000-000000000001', 'YouTube', 'https://www.youtube.com/embed/ExUMoVIpKnE', 1080, 0, true),
  ('e0000004-0002-0000-0000-000000000002', 'YouTube', 'https://www.youtube.com/embed/ExUMoVIpKnE', 720, 0, true),
  ('e0000004-0003-0000-0000-000000000003', 'YouTube', 'https://www.youtube.com/embed/ExUMoVIpKnE', 1080, 0, true);

-- SPY×FAMILY mirrors
INSERT INTO mirrors (episode_id, provider_name, embed_url, quality_label, priority, is_active) VALUES
  ('e0000005-0001-0000-0000-000000000001', 'YouTube', 'https://www.youtube.com/embed/_VRxEEBa1XU', 1080, 0, true),
  ('e0000005-0002-0000-0000-000000000002', 'YouTube', 'https://www.youtube.com/embed/_VRxEEBa1XU', 720, 0, true),
  ('e0000005-0003-0000-0000-000000000003', 'YouTube', 'https://www.youtube.com/embed/_VRxEEBa1XU', 1080, 0, true);

-- Chainsaw Man mirrors
INSERT INTO mirrors (episode_id, provider_name, embed_url, quality_label, priority, is_active) VALUES
  ('e0000006-0001-0000-0000-000000000001', 'YouTube', 'https://www.youtube.com/embed/q15CRdE5Bv0', 1080, 0, true),
  ('e0000006-0002-0000-0000-000000000002', 'YouTube', 'https://www.youtube.com/embed/q15CRdE5Bv0', 720, 0, true),
  ('e0000006-0003-0000-0000-000000000003', 'YouTube', 'https://www.youtube.com/embed/q15CRdE5Bv0', 1080, 0, true);

-- Mob Psycho 100 mirrors
INSERT INTO mirrors (episode_id, provider_name, embed_url, quality_label, priority, is_active) VALUES
  ('e0000007-0001-0000-0000-000000000001', 'YouTube', 'https://www.youtube.com/embed/Bw-5Lka-7Lk', 1080, 0, true),
  ('e0000007-0002-0000-0000-000000000002', 'YouTube', 'https://www.youtube.com/embed/Bw-5Lka-7Lk', 720, 0, true),
  ('e0000007-0003-0000-0000-000000000003', 'YouTube', 'https://www.youtube.com/embed/Bw-5Lka-7Lk', 1080, 0, true);

-- Vinland Saga mirrors
INSERT INTO mirrors (episode_id, provider_name, embed_url, quality_label, priority, is_active) VALUES
  ('e0000008-0001-0000-0000-000000000001', 'YouTube', 'https://www.youtube.com/embed/BRubJuMCUkI', 1080, 0, true),
  ('e0000008-0002-0000-0000-000000000002', 'YouTube', 'https://www.youtube.com/embed/BRubJuMCUkI', 720, 0, true),
  ('e0000008-0003-0000-0000-000000000003', 'YouTube', 'https://www.youtube.com/embed/BRubJuMCUkI', 1080, 0, true);

COMMIT;

-- ─── Verification queries ──────────────────────────────────────────────────
SELECT '=== Series ===' AS info;
SELECT slug, title, status, score FROM series ORDER BY score DESC;

SELECT '=== Episodes per series ===' AS info;
SELECT s.slug, COUNT(e.id) AS episodes
FROM series s
LEFT JOIN episodes e ON e.series_id = s.id
GROUP BY s.slug
ORDER BY s.slug;

SELECT '=== Mirrors per episode (first 10) ===' AS info;
SELECT s.slug, e.episode_number, e.title AS ep_title, m.provider_name, m.quality_label, m.is_active
FROM mirrors m
JOIN episodes e ON m.episode_id = e.id
JOIN series s ON e.series_id = s.id
ORDER BY s.slug, e.episode_number
LIMIT 10;
