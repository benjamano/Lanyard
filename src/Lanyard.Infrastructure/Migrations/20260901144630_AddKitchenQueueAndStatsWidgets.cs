using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKitchenQueueAndStatsWidgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReadyDate",
                table: "KitchenOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowStatusChanges",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxTickets",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowTakings",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StatsPeriod",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReadyDate",
                table: "KitchenOrders");

            migrationBuilder.DropColumn(
                name: "AllowStatusChanges",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "MaxTickets",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ShowTakings",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "StatsPeriod",
                table: "DashboardWidgets");
        }
    }
}
