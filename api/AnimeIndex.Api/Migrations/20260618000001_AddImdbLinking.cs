using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeIndex.Api.Migrations
{
    /// <summary>
    /// Adds IMDb/TMDB linking columns so each episode can deep-link to its IMDb page
    /// (where users rate it) and cache the IMDb rating shown in the UI.
    ///
    /// Like the other recent migrations in this project, this is hand-written idempotent SQL,
    /// applied to Supabase via the Management API at deploy time (not auto-run by MigrateAsync).
    /// </summary>
    public partial class AddImdbLinking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE series   ADD COLUMN IF NOT EXISTS tmdb_id integer;
ALTER TABLE series   ADD COLUMN IF NOT EXISTS imdb_id text;
ALTER TABLE series   ADD COLUMN IF NOT EXISTS imdb_resolved_at timestamp with time zone;

ALTER TABLE episodes ADD COLUMN IF NOT EXISTS imdb_id text;
ALTER TABLE episodes ADD COLUMN IF NOT EXISTS imdb_rating numeric(3,1);
ALTER TABLE episodes ADD COLUMN IF NOT EXISTS imdb_votes integer;
ALTER TABLE episodes ADD COLUMN IF NOT EXISTS imdb_checked_at timestamp with time zone;

-- Find series that still need resolving (cheap partial index).
CREATE INDEX IF NOT EXISTS idx_series_imdb_unresolved
    ON series(imdb_resolved_at) WHERE tmdb_id IS NULL;
-- Find episodes that need an IMDb id or a rating refresh.
CREATE INDEX IF NOT EXISTS idx_episodes_imdb_pending
    ON episodes(imdb_checked_at) WHERE imdb_id IS NOT NULL;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP INDEX IF EXISTS idx_series_imdb_unresolved;
DROP INDEX IF EXISTS idx_episodes_imdb_pending;
ALTER TABLE series   DROP COLUMN IF EXISTS tmdb_id;
ALTER TABLE series   DROP COLUMN IF EXISTS imdb_id;
ALTER TABLE series   DROP COLUMN IF EXISTS imdb_resolved_at;
ALTER TABLE episodes DROP COLUMN IF EXISTS imdb_id;
ALTER TABLE episodes DROP COLUMN IF EXISTS imdb_rating;
ALTER TABLE episodes DROP COLUMN IF EXISTS imdb_votes;
ALTER TABLE episodes DROP COLUMN IF EXISTS imdb_checked_at;
");
        }
    }
}
