using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeIndex.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddInstagramPosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "instagram_posts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "published"),
                    ig_media_id = table.Column<string>(type: "text", nullable: true),
                    caption = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_instagram_posts", x => x.id);
                    table.CheckConstraint("CK_instagram_posts_type", "post_type IN ('story','feed','carousel_item')");
                    table.CheckConstraint("CK_instagram_posts_status", "status IN ('published','failed','skipped')");
                    table.ForeignKey(
                        name: "FK_instagram_posts_episodes_episode_id",
                        column: x => x.episode_id,
                        principalTable: "episodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_instagram_posts_episode",
                table: "instagram_posts",
                column: "episode_id");

            migrationBuilder.CreateIndex(
                name: "idx_instagram_posts_created",
                table: "instagram_posts",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "instagram_posts");
        }
    }
}
