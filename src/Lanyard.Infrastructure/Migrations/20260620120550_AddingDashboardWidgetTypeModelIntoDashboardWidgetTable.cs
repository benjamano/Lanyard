using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddingDashboardWidgetTypeModelIntoDashboardWidgetTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClientId",
                table: "DashboardWidgets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowCurrentGameStatus",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowTimeLeft",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ShowCurrentGameStatus",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ShowTimeLeft",
                table: "DashboardWidgets");
        }
    }
}
