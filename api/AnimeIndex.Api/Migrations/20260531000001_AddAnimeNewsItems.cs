using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeIndex.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAnimeNewsItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS anime_news_items (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    source_key text NOT NULL,
    rss_guid text NOT NULL,
    title text NOT NULL,
    summary text,
    image_url text,
    article_url text NOT NULL,
    published_at timestamp with time zone NOT NULL,
    fetched_at timestamp with time zone NOT NULL DEFAULT now(),
    ig_post_status text NOT NULL DEFAULT 'pending',
    ig_feed_media_id text,
    ig_story_media_id text,
    ig_posted_at timestamp with time zone,
    error_message text,
    CONSTRAINT ""PK_anime_news_items"" PRIMARY KEY (id),
    CONSTRAINT ""CK_anime_news_items_status""
        CHECK (ig_post_status IN ('pending','published','skipped','failed')),
    CONSTRAINT ""uq_anime_news_source_guid"" UNIQUE (source_key, rss_guid)
);
CREATE INDEX IF NOT EXISTS idx_anime_news_ig_status ON anime_news_items(ig_post_status);
CREATE INDEX IF NOT EXISTS idx_anime_news_published_at ON anime_news_items(published_at DESC);
-- RLS default-deny like every other table (only the scraper's privileged conn writes it).
-- Was missing originally and flagged by Supabase as ""Table publicly accessible""; enabled in prod 2026-06-22.
ALTER TABLE anime_news_items ENABLE ROW LEVEL SECURITY;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS anime_news_items;");
        }
    }
}
