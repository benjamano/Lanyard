using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKioskOfflineFallback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing kiosks get the same defaults as new ones (KioskOfflineLimits): offline
            // fallback on, after 30 seconds, with a 2 GB cache.
            migrationBuilder.AddColumn<int>(
                name: "OfflineAfterSeconds",
                table: "Clients",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<bool>(
                name: "OfflineFallbackEnabled",
                table: "Clients",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "ProjectionCacheLimitMb",
                table: "Clients",
                type: "integer",
                nullable: false,
                defaultValue: 2048);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OfflineAfterSeconds",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "OfflineFallbackEnabled",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "ProjectionCacheLimitMb",
                table: "Clients");
        }
    }
}
