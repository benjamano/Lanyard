using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddComplianceSummaryWidget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ComplianceSummaryWidget_ExpiryWindowDays",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ComplianceSummaryWidget_LocationId",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ComplianceSummaryWidget_MaxItems",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ComplianceSummaryWidget_ExpiryWindowDays",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ComplianceSummaryWidget_LocationId",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ComplianceSummaryWidget_MaxItems",
                table: "DashboardWidgets");
        }
    }
}
