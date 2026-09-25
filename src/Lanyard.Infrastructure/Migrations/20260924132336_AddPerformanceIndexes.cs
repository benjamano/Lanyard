using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AutomationRuleExecutions_AutomationRuleId",
                table: "AutomationRuleExecutions");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_BpmAnalysisStatus",
                table: "Songs",
                column: "BpmAnalysisStatus");

            migrationBuilder.CreateIndex(
                name: "IX_CourseAssignments_UserId",
                table: "CourseAssignments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuleExecutions_AutomationRuleId_ExecutedAt",
                table: "AutomationRuleExecutions",
                columns: new[] { "AutomationRuleId", "ExecutedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuleExecutions_ExecutedAt",
                table: "AutomationRuleExecutions",
                column: "ExecutedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Songs_BpmAnalysisStatus",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_CourseAssignments_UserId",
                table: "CourseAssignments");

            migrationBuilder.DropIndex(
                name: "IX_AutomationRuleExecutions_AutomationRuleId_ExecutedAt",
                table: "AutomationRuleExecutions");

            migrationBuilder.DropIndex(
                name: "IX_AutomationRuleExecutions_ExecutedAt",
                table: "AutomationRuleExecutions");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuleExecutions_AutomationRuleId",
                table: "AutomationRuleExecutions",
                column: "AutomationRuleId");
        }
    }
}
