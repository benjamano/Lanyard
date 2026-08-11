using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCourseLocationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsShared",
                table: "Courses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "LocationId",
                table: "Courses",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Courses_LocationId",
                table: "Courses",
                column: "LocationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Courses_Locations_LocationId",
                table: "Courses",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Courses_Locations_LocationId",
                table: "Courses");

            migrationBuilder.DropIndex(
                name: "IX_Courses_LocationId",
                table: "Courses");

            migrationBuilder.DropColumn(
                name: "IsShared",
                table: "Courses");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "Courses");
        }
    }
}
