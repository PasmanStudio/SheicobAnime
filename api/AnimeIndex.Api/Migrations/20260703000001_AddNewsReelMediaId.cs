using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeIndex.Api.Migrations
{
    /// <summary>
    /// Adds ig_reel_media_id to anime_news_items: marca qué noticia fue el Reel
    /// diario (motion card animada) — el publisher publica como máximo un reel
    /// de noticias cada 24 h y esta columna es el dedup.
    ///
    /// Like the other recent migrations in this project, this is hand-written
    /// idempotent SQL.
    /// </summary>
    public partial class AddNewsReelMediaId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE anime_news_items ADD COLUMN IF NOT EXISTS ig_reel_media_id text;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE anime_news_items DROP COLUMN IF EXISTS ig_reel_media_id;
");
        }
    }
}
