using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddingMusicDashboardWidgetTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MusicPlaylistSelectorWidget_ClientId",
                table: "DashboardWidgets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MusicTimelineWidget_ClientId",
                table: "DashboardWidgets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowSongTitle",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MusicPlaylistSelectorWidget_ClientId",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "MusicTimelineWidget_ClientId",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ShowSongTitle",
                table: "DashboardWidgets");
        }
    }
}
