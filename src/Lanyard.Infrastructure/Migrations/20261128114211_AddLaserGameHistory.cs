using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLaserGameHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "StartedAtUtc",
                table: "GameResults",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GameHistoryCount",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowGameHistory",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);

            // The columns are shared by every widget type, so they can't be NOT NULL, but Laser game
            // status widgets read them as a plain bool and int: give the existing ones history off
            // and the default count rather than NULL.
            migrationBuilder.Sql("UPDATE \"DashboardWidgets\" SET \"ShowGameHistory\" = FALSE, \"GameHistoryCount\" = 5 WHERE \"Type\" = 3;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StartedAtUtc",
                table: "GameResults");

            migrationBuilder.DropColumn(
                name: "GameHistoryCount",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ShowGameHistory",
                table: "DashboardWidgets");
        }
    }
}
