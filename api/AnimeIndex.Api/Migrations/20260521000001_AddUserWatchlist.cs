using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeIndex.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddUserWatchlist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // user_watch_entries — per-user anime watchlist (status: mirando/visto/por_ver/favorito/dropped)
            // user_episode_history — per-user episode watch log
            // Both reference users.id from Auth.js via plain TEXT (no FK constraint — cross-schema)
            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS user_watch_entries (
    user_id         TEXT        NOT NULL,
    series_slug     TEXT        NOT NULL,
    series_title    TEXT        NOT NULL DEFAULT '',
    cover_url       TEXT,
    status          TEXT        NOT NULL CHECK (status IN ('mirando','visto','por_ver','favorito','dropped')),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, series_slug)
);

CREATE TABLE IF NOT EXISTS user_episode_history (
    user_id         TEXT        NOT NULL,
    episode_id      UUID        NOT NULL,
    series_slug     TEXT        NOT NULL,
    episode_number  INT         NOT NULL,
    episode_title   TEXT,
    series_title    TEXT        NOT NULL DEFAULT '',
    cover_url       TEXT,
    watched_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, episode_id)
);

-- Fast lookups: all entries for a user sorted by recency
CREATE INDEX IF NOT EXISTS idx_uwe_user_updated  ON user_watch_entries (user_id, updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_uwe_user_status   ON user_watch_entries (user_id, status);
CREATE INDEX IF NOT EXISTS idx_ueh_user_watched  ON user_episode_history (user_id, watched_at DESC);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS user_episode_history;
DROP TABLE IF EXISTS user_watch_entries;
");
        }
    }
}
