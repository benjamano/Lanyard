using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCourseAssignmentLocationField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LocationId",
                table: "CourseAssignments",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CourseAssignments_LocationId",
                table: "CourseAssignments",
                column: "LocationId");

            migrationBuilder.AddForeignKey(
                name: "FK_CourseAssignments_Locations_LocationId",
                table: "CourseAssignments",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CourseAssignments_Locations_LocationId",
                table: "CourseAssignments");

            migrationBuilder.DropIndex(
                name: "IX_CourseAssignments_LocationId",
                table: "CourseAssignments");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "CourseAssignments");
        }
    }
}
