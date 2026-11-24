using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKioskEditableTextWidget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EditableOnKiosk",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "KioskAutoSave",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);

            // The columns are shared by every widget type (one table), so they're nullable, but
            // a Text widget's flags are plain bools: existing Text widgets start with both off.
            migrationBuilder.Sql("UPDATE \"DashboardWidgets\" SET \"EditableOnKiosk\" = FALSE, \"KioskAutoSave\" = FALSE WHERE \"Type\" = 2;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EditableOnKiosk",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "KioskAutoSave",
                table: "DashboardWidgets");
        }
    }
}
