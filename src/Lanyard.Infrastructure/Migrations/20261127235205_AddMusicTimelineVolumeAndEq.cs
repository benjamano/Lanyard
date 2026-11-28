using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMusicTimelineVolumeAndEq : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowVolumeAndEq",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);

            // The column is shared by every widget type, so it can't be NOT NULL, but Now playing
            // widgets read it as a plain bool: give the existing ones "off" rather than NULL.
            migrationBuilder.Sql("UPDATE \"DashboardWidgets\" SET \"ShowVolumeAndEq\" = FALSE WHERE \"Type\" = 7;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShowVolumeAndEq",
                table: "DashboardWidgets");
        }
    }
}
