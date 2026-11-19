using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsOnTodayWidget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WhatsOnTodayWidget_LocationId",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WhatsOnTodayWidget_MaxItems",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WhatsOnTodayWidget_LocationId",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "WhatsOnTodayWidget_MaxItems",
                table: "DashboardWidgets");
        }
    }
}
