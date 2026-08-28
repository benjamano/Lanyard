using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMyTrainingWidget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IncludeCompleted",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxItems",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IncludeCompleted",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "MaxItems",
                table: "DashboardWidgets");
        }
    }
}
