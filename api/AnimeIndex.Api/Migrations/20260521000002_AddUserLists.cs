using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeIndex.Api.Migrations
{
    public partial class AddUserLists : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS user_lists (
    id          UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     TEXT        NOT NULL,
    name        TEXT        NOT NULL,
    description TEXT,
    is_public   BOOLEAN     NOT NULL DEFAULT false,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS user_list_items (
    list_id      UUID        NOT NULL REFERENCES user_lists(id) ON DELETE CASCADE,
    series_slug  TEXT        NOT NULL,
    series_title TEXT        NOT NULL DEFAULT '',
    cover_url    TEXT,
    added_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (list_id, series_slug)
);

CREATE INDEX IF NOT EXISTS idx_user_lists_user   ON user_lists(user_id, updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_user_list_items   ON user_list_items(list_id, added_at DESC);
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS user_list_items;
DROP TABLE IF EXISTS user_lists;
");
        }
    }
}
