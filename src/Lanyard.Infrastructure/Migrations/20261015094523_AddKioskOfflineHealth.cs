using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKioskOfflineHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastOfflineEndedUtc",
                table: "ClientHealthSnapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastOfflineStartedUtc",
                table: "ClientHealthSnapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OfflineCacheBytes",
                table: "ClientHealthSnapshots",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OfflineCacheSyncedUtc",
                table: "ClientHealthSnapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OfflineFallbackReady",
                table: "ClientHealthSnapshots",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OfflineVideosNotCached",
                table: "ClientHealthSnapshots",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastOfflineEndedUtc",
                table: "ClientHealthSnapshots");

            migrationBuilder.DropColumn(
                name: "LastOfflineStartedUtc",
                table: "ClientHealthSnapshots");

            migrationBuilder.DropColumn(
                name: "OfflineCacheBytes",
                table: "ClientHealthSnapshots");

            migrationBuilder.DropColumn(
                name: "OfflineCacheSyncedUtc",
                table: "ClientHealthSnapshots");

            migrationBuilder.DropColumn(
                name: "OfflineFallbackReady",
                table: "ClientHealthSnapshots");

            migrationBuilder.DropColumn(
                name: "OfflineVideosNotCached",
                table: "ClientHealthSnapshots");
        }
    }
}
