using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationIdleTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IdleThresholdMinutes",
                table: "AutomationRules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TriggerType",
                table: "AutomationRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IdleThresholdMinutes",
                table: "AutomationRules");

            migrationBuilder.DropColumn(
                name: "TriggerType",
                table: "AutomationRules");
        }
    }
}
