using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKioskAlertState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttentionAlertReasons",
                table: "ClientHealthSnapshots",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DegradedSinceUtc",
                table: "ClientHealthSnapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OfflineAlertedSinceUtc",
                table: "ClientHealthSnapshots",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttentionAlertReasons",
                table: "ClientHealthSnapshots");

            migrationBuilder.DropColumn(
                name: "DegradedSinceUtc",
                table: "ClientHealthSnapshots");

            migrationBuilder.DropColumn(
                name: "OfflineAlertedSinceUtc",
                table: "ClientHealthSnapshots");
        }
    }
}
