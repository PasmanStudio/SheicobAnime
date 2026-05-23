using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeIndex.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramPosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS telegram_posts (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    episode_id uuid NOT NULL,
    telegram_message_id text,
    status text NOT NULL DEFAULT 'published',
    error_message text,
    created_at timestamp with time zone NOT NULL DEFAULT now(),
    published_at timestamp with time zone,
    CONSTRAINT ""PK_telegram_posts"" PRIMARY KEY (id),
    CONSTRAINT ""CK_telegram_posts_status"" CHECK (status IN ('published','failed')),
    CONSTRAINT ""FK_telegram_posts_episodes_episode_id""
        FOREIGN KEY (episode_id) REFERENCES episodes(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_telegram_posts_episode ON telegram_posts(episode_id);
CREATE INDEX IF NOT EXISTS idx_telegram_posts_created ON telegram_posts(created_at);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS telegram_posts;");
        }
    }
}
