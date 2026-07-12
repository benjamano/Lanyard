using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddingButtonWidgetDisplayIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DisplayIndex",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayIndex",
                table: "DashboardWidgets");
        }
    }
}
