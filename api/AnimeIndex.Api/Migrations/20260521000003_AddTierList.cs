using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeIndex.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTierList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS user_tier_lists (
    id          UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     TEXT        NOT NULL,
    name        TEXT        NOT NULL DEFAULT 'Mi Tier List',
    is_public   BOOLEAN     NOT NULL DEFAULT false,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS user_tier_entries (
    tier_list_id UUID        NOT NULL REFERENCES user_tier_lists(id) ON DELETE CASCADE,
    series_slug  TEXT        NOT NULL,
    series_title TEXT        NOT NULL DEFAULT '',
    cover_url    TEXT,
    tier         TEXT        NOT NULL CHECK (tier IN ('S','A','B','C','D','F')),
    position     INT         NOT NULL DEFAULT 0,
    added_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tier_list_id, series_slug)
);

CREATE INDEX IF NOT EXISTS idx_tier_lists_user   ON user_tier_lists(user_id, updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_tier_entries_list ON user_tier_entries(tier_list_id, tier, position);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS user_tier_entries;
DROP TABLE IF EXISTS user_tier_lists;
");
        }
    }
}
