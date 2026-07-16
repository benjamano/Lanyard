using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddingClientAutoRestartSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoRestartEnabled",
                table: "Clients",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AutoRestartIntervalCount",
                table: "Clients",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AutoRestartIntervalUnit",
                table: "Clients",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "AutoRestartTimeOfDay",
                table: "Clients",
                type: "time without time zone",
                nullable: false,
                defaultValue: new TimeOnly(0, 0, 0));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoRestartEnabled",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "AutoRestartIntervalCount",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "AutoRestartIntervalUnit",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "AutoRestartTimeOfDay",
                table: "Clients");
        }
    }
}
