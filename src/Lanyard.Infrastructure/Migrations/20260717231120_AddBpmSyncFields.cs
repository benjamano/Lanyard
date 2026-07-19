using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBpmSyncFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Bpm",
                table: "Songs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BpmAnalysisDate",
                table: "Songs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BpmAnalysisStatus",
                table: "Songs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "FirstBeatOffsetSeconds",
                table: "Songs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Beats",
                table: "DmxSceneSteps",
                type: "double precision",
                nullable: false,
                defaultValue: 1.0);

            migrationBuilder.AddColumn<bool>(
                name: "BpmSyncEnabled",
                table: "DmxScenes",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Bpm",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "BpmAnalysisDate",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "BpmAnalysisStatus",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "FirstBeatOffsetSeconds",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "Beats",
                table: "DmxSceneSteps");

            migrationBuilder.DropColumn(
                name: "BpmSyncEnabled",
                table: "DmxScenes");
        }
    }
}
