using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeIndex.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesMetadataColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "aired_date",
                table: "series",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "demographics",
                table: "series",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "duration_minutes",
                table: "series",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "language",
                table: "series",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "quality",
                table: "series",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "season",
                table: "series",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "studio",
                table: "series",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "aired_date",
                table: "series");

            migrationBuilder.DropColumn(
                name: "demographics",
                table: "series");

            migrationBuilder.DropColumn(
                name: "duration_minutes",
                table: "series");

            migrationBuilder.DropColumn(
                name: "language",
                table: "series");

            migrationBuilder.DropColumn(
                name: "quality",
                table: "series");

            migrationBuilder.DropColumn(
                name: "season",
                table: "series");

            migrationBuilder.DropColumn(
                name: "studio",
                table: "series");
        }
    }
}
