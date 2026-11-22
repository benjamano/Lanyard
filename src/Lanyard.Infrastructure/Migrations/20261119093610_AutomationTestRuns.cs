using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AutomationTestRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTestRun",
                table: "AutomationRuleExecutions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TestRunByUserId",
                table: "AutomationRuleExecutions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsTestRun",
                table: "AutomationRuleExecutions");

            migrationBuilder.DropColumn(
                name: "TestRunByUserId",
                table: "AutomationRuleExecutions");
        }
    }
}
