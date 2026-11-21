using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationPartyTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PartyLeadMinutes",
                table: "AutomationRules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PartyLocationId",
                table: "AutomationRules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PartyMilestone",
                table: "AutomationRules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PartyRoom",
                table: "AutomationRules",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PartyBookingId",
                table: "AutomationRuleExecutions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuleExecutions_AutomationRuleId_PartyBookingId",
                table: "AutomationRuleExecutions",
                columns: new[] { "AutomationRuleId", "PartyBookingId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuleExecutions_PartyBookingId",
                table: "AutomationRuleExecutions",
                column: "PartyBookingId");

            migrationBuilder.AddForeignKey(
                name: "FK_AutomationRuleExecutions_PartyBookings_PartyBookingId",
                table: "AutomationRuleExecutions",
                column: "PartyBookingId",
                principalTable: "PartyBookings",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AutomationRuleExecutions_PartyBookings_PartyBookingId",
                table: "AutomationRuleExecutions");

            migrationBuilder.DropIndex(
                name: "IX_AutomationRuleExecutions_AutomationRuleId_PartyBookingId",
                table: "AutomationRuleExecutions");

            migrationBuilder.DropIndex(
                name: "IX_AutomationRuleExecutions_PartyBookingId",
                table: "AutomationRuleExecutions");

            migrationBuilder.DropColumn(
                name: "PartyLeadMinutes",
                table: "AutomationRules");

            migrationBuilder.DropColumn(
                name: "PartyLocationId",
                table: "AutomationRules");

            migrationBuilder.DropColumn(
                name: "PartyMilestone",
                table: "AutomationRules");

            migrationBuilder.DropColumn(
                name: "PartyRoom",
                table: "AutomationRules");

            migrationBuilder.DropColumn(
                name: "PartyBookingId",
                table: "AutomationRuleExecutions");
        }
    }
}
