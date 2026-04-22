-- =============================================================
-- One-time cleanup: normalize provider names + remove blocked mirrors
-- Run in Supabase SQL editor after deploying the new scraper version.
-- =============================================================

-- Delete mega mirrors (blocked provider that slipped through)
DELETE FROM mirrors WHERE provider_name = 'mega';

-- Normalize provider names for existing records
UPDATE mirrors SET provider_name = 'streamwish' WHERE provider_name = 'bysekoze';
UPDATE mirrors SET provider_name = 'mixdrop'    WHERE provider_name = 'mxdrop';
UPDATE mirrors SET provider_name = 'vidhide'    WHERE provider_name = 'dsvplay';

-- Verify after running:
-- SELECT provider_name, COUNT(*) FROM mirrors GROUP BY provider_name ORDER BY COUNT(*) DESC;
