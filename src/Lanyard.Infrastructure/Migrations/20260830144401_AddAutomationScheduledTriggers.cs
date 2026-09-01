using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationScheduledTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScheduledDaysOfWeek",
                table: "AutomationRules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "ScheduledTimeOfDay",
                table: "AutomationRules",
                type: "time without time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScheduledDaysOfWeek",
                table: "AutomationRules");

            migrationBuilder.DropColumn(
                name: "ScheduledTimeOfDay",
                table: "AutomationRules");
        }
    }
}
