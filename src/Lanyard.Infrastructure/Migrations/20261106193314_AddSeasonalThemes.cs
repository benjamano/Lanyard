using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSeasonalThemes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SeasonalThemeSetting",
                table: "Companies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ShowSeasonalThemes",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                // Existing staff start opted in, matching the model's default for new accounts.
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SeasonalThemeSetting",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "ShowSeasonalThemes",
                table: "AspNetUsers");
        }
    }
}
