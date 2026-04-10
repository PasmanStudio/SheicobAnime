using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AnimeIndex.Api.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blocked_slugs",
                columns: table => new
                {
                    slug = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    blocked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blocked_slugs", x => x.slug);
                });

            migrationBuilder.CreateTable(
                name: "genres",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_genres", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "series",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    slug = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    title_romaji = table.Column<string>(type: "text", nullable: true),
                    title_native = table.Column<string>(type: "text", nullable: true),
                    synopsis = table.Column<string>(type: "text", nullable: true),
                    cover_url = table.Column<string>(type: "text", nullable: true),
                    banner_url = table.Column<string>(type: "text", nullable: true),
                    year = table.Column<short>(type: "smallint", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true),
                    type = table.Column<string>(type: "text", nullable: true),
                    score = table.Column<decimal>(type: "numeric(4,2)", nullable: true),
                    episode_count = table.Column<short>(type: "smallint", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'"),
                    last_scraped_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_series", x => x.id);
                    table.CheckConstraint("CK_series_status", "status IN ('ongoing','completed','upcoming','hiatus')");
                    table.CheckConstraint("CK_series_type", "type IN ('tv','movie','ova','ona','special')");
                });

            migrationBuilder.CreateTable(
                name: "episodes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    episode_number = table.Column<short>(type: "smallint", nullable: false),
                    title = table.Column<string>(type: "text", nullable: true),
                    thumbnail_url = table.Column<string>(type: "text", nullable: true),
                    duration_secs = table.Column<short>(type: "smallint", nullable: true),
                    aired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_published = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_episodes", x => x.id);
                    table.ForeignKey(
                        name: "FK_episodes_series_series_id",
                        column: x => x.series_id,
                        principalTable: "series",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scrape_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    series_id = table.Column<Guid>(type: "uuid", nullable: true),
                    job_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "pending"),
                    attempt_count = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    scheduled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scrape_jobs", x => x.id);
                    table.CheckConstraint("CK_scrape_jobs_status", "status IN ('pending','running','completed','failed','dead_letter')");
                    table.ForeignKey(
                        name: "FK_scrape_jobs_series_series_id",
                        column: x => x.series_id,
                        principalTable: "series",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "series_genres",
                columns: table => new
                {
                    series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    genre_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_series_genres", x => new { x.series_id, x.genre_id });
                    table.ForeignKey(
                        name: "FK_series_genres_genres_genre_id",
                        column: x => x.genre_id,
                        principalTable: "genres",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_series_genres_series_series_id",
                        column: x => x.series_id,
                        principalTable: "series",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mirrors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_name = table.Column<string>(type: "text", nullable: false),
                    embed_url = table.Column<string>(type: "text", nullable: false),
                    quality_label = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)720),
                    priority = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    consecutive_failures = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    last_checked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mirrors", x => x.id);
                    table.ForeignKey(
                        name: "FK_mirrors_episodes_episode_id",
                        column: x => x.episode_id,
                        principalTable: "episodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_episodes_series",
                table: "episodes",
                columns: new[] { "series_id", "episode_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_genres_name",
                table: "genres",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_mirrors_episode_active",
                table: "mirrors",
                columns: new[] { "episode_id", "priority" },
                filter: "is_active = true");

            migrationBuilder.CreateIndex(
                name: "IX_mirrors_episode_id_embed_url",
                table: "mirrors",
                columns: new[] { "episode_id", "embed_url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_scrape_jobs_series_id",
                table: "scrape_jobs",
                column: "series_id");

            migrationBuilder.CreateIndex(
                name: "idx_series_slug",
                table: "series",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_series_status_year",
                table: "series",
                columns: new[] { "status", "year", "score" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_series_genres_genre_id",
                table: "series_genres",
                column: "genre_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blocked_slugs");

            migrationBuilder.DropTable(
                name: "mirrors");

            migrationBuilder.DropTable(
                name: "scrape_jobs");

            migrationBuilder.DropTable(
                name: "series_genres");

            migrationBuilder.DropTable(
                name: "episodes");

            migrationBuilder.DropTable(
                name: "genres");

            migrationBuilder.DropTable(
                name: "series");
        }
    }
}
