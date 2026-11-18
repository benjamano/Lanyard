using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingImportSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActiveEndHour",
                table: "BookingImportSettings",
                type: "integer",
                nullable: false,
                defaultValue: 23);

            migrationBuilder.AddColumn<int>(
                name: "ActiveStartHour",
                table: "BookingImportSettings",
                type: "integer",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<int>(
                name: "FrequencyHours",
                table: "BookingImportSettings",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveEndHour",
                table: "BookingImportSettings");

            migrationBuilder.DropColumn(
                name: "ActiveStartHour",
                table: "BookingImportSettings");

            migrationBuilder.DropColumn(
                name: "FrequencyHours",
                table: "BookingImportSettings");
        }
    }
}
