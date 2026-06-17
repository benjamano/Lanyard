using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDashboardWidgetSubtypeColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Content",
                table: "DashboardWidgets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Is24HourFormat",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowDate",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowMilliSeconds",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Content",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "Is24HourFormat",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ShowDate",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ShowMilliSeconds",
                table: "DashboardWidgets");
        }
    }
}
