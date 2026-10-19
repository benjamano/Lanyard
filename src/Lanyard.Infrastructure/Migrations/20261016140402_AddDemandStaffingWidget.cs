using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDemandStaffingWidget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DemandStaffingWidget_LocationId",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DemandStaffingWidget_RangeDays",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DemandStaffingWidget_LocationId",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "DemandStaffingWidget_RangeDays",
                table: "DashboardWidgets");
        }
    }
}
