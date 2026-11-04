using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationGameDataTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TriggerScoreScope",
                table: "AutomationRules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TriggerTeam",
                table: "AutomationRules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TriggerThreshold",
                table: "AutomationRules",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TriggerScoreScope",
                table: "AutomationRules");

            migrationBuilder.DropColumn(
                name: "TriggerTeam",
                table: "AutomationRules");

            migrationBuilder.DropColumn(
                name: "TriggerThreshold",
                table: "AutomationRules");
        }
    }
}
