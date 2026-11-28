using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChecklistReadingRanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MaxReading",
                table: "ChecklistTasks",
                type: "numeric(9,2)",
                precision: 9,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MinReading",
                table: "ChecklistTasks",
                type: "numeric(9,2)",
                precision: 9,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReadingAlert",
                table: "ChecklistTasks",
                type: "integer",
                nullable: false,
                defaultValue: 2); // ChecklistReadingAlert.AlertAndNotify, the model's default; existing tasks have no range yet

            migrationBuilder.AddColumn<decimal>(
                name: "MaxReading",
                table: "ChecklistRunItems",
                type: "numeric(9,2)",
                precision: 9,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MinReading",
                table: "ChecklistRunItems",
                type: "numeric(9,2)",
                precision: 9,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReadingAlert",
                table: "ChecklistRunItems",
                type: "integer",
                nullable: false,
                defaultValue: 2); // ChecklistReadingAlert.AlertAndNotify, the model's default; existing tasks have no range yet
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxReading",
                table: "ChecklistTasks");

            migrationBuilder.DropColumn(
                name: "MinReading",
                table: "ChecklistTasks");

            migrationBuilder.DropColumn(
                name: "ReadingAlert",
                table: "ChecklistTasks");

            migrationBuilder.DropColumn(
                name: "MaxReading",
                table: "ChecklistRunItems");

            migrationBuilder.DropColumn(
                name: "MinReading",
                table: "ChecklistRunItems");

            migrationBuilder.DropColumn(
                name: "ReadingAlert",
                table: "ChecklistRunItems");
        }
    }
}
