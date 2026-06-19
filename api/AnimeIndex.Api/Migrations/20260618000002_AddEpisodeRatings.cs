using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeIndex.Api.Migrations
{
    /// <summary>
    /// Native per-episode star rating (1–5), keyed by device id (works for anonymous and
    /// logged-in viewers, like watch_progress). Hand-written idempotent SQL applied to Supabase
    /// at deploy time (these recent migrations don't auto-run via MigrateAsync).
    /// </summary>
    public partial class AddEpisodeRatings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS episode_ratings (
    device_id  uuid     NOT NULL,
    episode_id uuid     NOT NULL REFERENCES episodes(id) ON DELETE CASCADE,
    rating     smallint NOT NULL CONSTRAINT ""CK_episode_ratings_rating"" CHECK (rating BETWEEN 1 AND 5),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (device_id, episode_id)
);
CREATE INDEX IF NOT EXISTS idx_episode_ratings_episode ON episode_ratings(episode_id);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS episode_ratings;");
        }
    }
}
