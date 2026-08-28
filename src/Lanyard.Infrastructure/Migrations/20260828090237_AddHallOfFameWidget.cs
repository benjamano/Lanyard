using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHallOfFameWidget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HallOfFameWidget_ClientId",
                table: "DashboardWidgets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Period",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowBestAccuracy",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowBestTeam",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowTopScore",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HallOfFameWidget_ClientId",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "Period",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ShowBestAccuracy",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ShowBestTeam",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ShowTopScore",
                table: "DashboardWidgets");
        }
    }
}
