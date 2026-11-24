using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDashboardDesignerLayoutOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HideOnPhone",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PhoneOrder",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowTitle",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "FitToScreen",
                table: "Dashboards",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RowHeight",
                table: "Dashboards",
                type: "integer",
                nullable: false,
                // Existing dashboards keep the 120px rows the grid has always used.
                defaultValue: 120);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HideOnPhone",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "PhoneOrder",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ShowTitle",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "FitToScreen",
                table: "Dashboards");

            migrationBuilder.DropColumn(
                name: "RowHeight",
                table: "Dashboards");
        }
    }
}
