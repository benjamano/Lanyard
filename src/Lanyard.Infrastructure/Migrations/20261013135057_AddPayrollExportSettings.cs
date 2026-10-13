using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPayrollExportSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing settings rows get the same values a new row starts with: monthly pay
            // periods on the 1st and no break deduction, so the export behaves exactly as before
            // until someone changes it. The 20 min / 6 h pair is only used once the fixed rule is chosen.
            migrationBuilder.AddColumn<int>(
                name: "BreakDeduction",
                table: "CompanySchedulingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "BreakDeductionAfterHours",
                table: "CompanySchedulingSettings",
                type: "numeric",
                nullable: false,
                defaultValue: 6m);

            migrationBuilder.AddColumn<int>(
                name: "BreakDeductionMinutes",
                table: "CompanySchedulingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 20);

            migrationBuilder.AddColumn<int>(
                name: "PayPeriod",
                table: "CompanySchedulingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PayPeriodAnchorDate",
                table: "CompanySchedulingSettings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PayPeriodStartDay",
                table: "CompanySchedulingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BreakDeduction",
                table: "CompanySchedulingSettings");

            migrationBuilder.DropColumn(
                name: "BreakDeductionAfterHours",
                table: "CompanySchedulingSettings");

            migrationBuilder.DropColumn(
                name: "BreakDeductionMinutes",
                table: "CompanySchedulingSettings");

            migrationBuilder.DropColumn(
                name: "PayPeriod",
                table: "CompanySchedulingSettings");

            migrationBuilder.DropColumn(
                name: "PayPeriodAnchorDate",
                table: "CompanySchedulingSettings");

            migrationBuilder.DropColumn(
                name: "PayPeriodStartDay",
                table: "CompanySchedulingSettings");
        }
    }
}
