using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeIndex.Api.Migrations
{
    /// <inheritdoc />
    public partial class EnsureInstagramPosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent repair: creates the table if the original AddInstagramPosts
            // migration was recorded in __EFMigrationsHistory but the DDL never committed
            // (observed when running against a Supabase transaction-pooler connection).
            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS instagram_posts (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    episode_id uuid NOT NULL,
    post_type text NOT NULL,
    status text NOT NULL DEFAULT 'published',
    ig_media_id text,
    caption text,
    error_message text,
    created_at timestamp with time zone NOT NULL DEFAULT now(),
    published_at timestamp with time zone,
    CONSTRAINT ""PK_instagram_posts"" PRIMARY KEY (id),
    CONSTRAINT ""CK_instagram_posts_type"" CHECK (post_type IN ('story','feed','carousel_item')),
    CONSTRAINT ""CK_instagram_posts_status"" CHECK (status IN ('published','failed','skipped')),
    CONSTRAINT ""FK_instagram_posts_episodes_episode_id""
        FOREIGN KEY (episode_id) REFERENCES episodes(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_instagram_posts_episode ON instagram_posts(episode_id);
CREATE INDEX IF NOT EXISTS idx_instagram_posts_created ON instagram_posts(created_at);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: the original AddInstagramPosts.Down() handles the DROP TABLE.
        }
    }
}
