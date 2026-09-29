using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddShiftRemindersAndSchedulingWidgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderSentForStartUtc",
                table: "Shifts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DaysAhead",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MyShiftsWidget_MaxItems",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PendingTimeOffWidget_MaxItems",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowClockStatus",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowTimeOff",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WhoIsOnTodayWidget_LocationId",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SendShiftReminders",
                table: "CompanySchedulingSettings",
                type: "boolean",
                nullable: false,
                // On for companies that already have settings, matching the model default.
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReminderSentForStartUtc",
                table: "Shifts");

            migrationBuilder.DropColumn(
                name: "DaysAhead",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "MyShiftsWidget_MaxItems",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "PendingTimeOffWidget_MaxItems",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ShowClockStatus",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ShowTimeOff",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "WhoIsOnTodayWidget_LocationId",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "SendShiftReminders",
                table: "CompanySchedulingSettings");
        }
    }
}
