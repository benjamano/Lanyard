using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationRuleIsEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AutomationRuleId",
                table: "DashboardWidgets",
                type: "uuid",
                nullable: true);

            // Existing rules were implicitly "enabled" before this column existed — default
            // them to true so the migration doesn't silently disable every rule already in use.
            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                table: "AutomationRules",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutomationRuleId",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "IsEnabled",
                table: "AutomationRules");
        }
    }
}
