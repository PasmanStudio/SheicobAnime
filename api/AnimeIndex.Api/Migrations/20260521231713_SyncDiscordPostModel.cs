using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeIndex.Api.Migrations
{
    /// <inheritdoc />
    public partial class SyncDiscordPostModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No-op: discord_posts was already created via raw SQL in 20260518000001_AddDiscordPosts.
            // This migration exists solely to register DiscordPost in the EF Core model snapshot
            // so that MigrateAsync() no longer raises PendingModelChangesWarning.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Table was created by the earlier raw SQL migration; leave it intact on rollback.
        }
    }
}
