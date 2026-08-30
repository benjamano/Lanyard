using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectionStatusWidget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProjectionStatusWidget_ClientId",
                table: "DashboardWidgets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProjectionStatusWidget_DisplayIndex",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowControls",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProjectionStatusWidget_ClientId",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ProjectionStatusWidget_DisplayIndex",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ShowControls",
                table: "DashboardWidgets");
        }
    }
}
