using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveCaptureWidget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LiveCaptureWidget_ClientId",
                table: "DashboardWidgets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LiveCaptureWidget_DeviceName",
                table: "DashboardWidgets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LiveCaptureWidget_EnableAudio",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LiveCaptureWidget_FillTile",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LiveCaptureWidget_ClientId",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "LiveCaptureWidget_DeviceName",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "LiveCaptureWidget_EnableAudio",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "LiveCaptureWidget_FillTile",
                table: "DashboardWidgets");
        }
    }
}
