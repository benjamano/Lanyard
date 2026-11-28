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
