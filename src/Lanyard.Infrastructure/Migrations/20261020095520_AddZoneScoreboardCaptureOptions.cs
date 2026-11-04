using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddZoneScoreboardCaptureOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PromiscuousCapture",
                table: "ZoneScoreboardSettings",
                type: "boolean",
                nullable: false,
                // Existing kiosks keep capturing as they did. EF always writes this column, so the
                // database default only ever applies to the rows that exist now.
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "UseCaptureFilter",
                table: "ZoneScoreboardSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PromiscuousCapture",
                table: "ZoneScoreboardSettings");

            migrationBuilder.DropColumn(
                name: "UseCaptureFilter",
                table: "ZoneScoreboardSettings");
        }
    }
}
