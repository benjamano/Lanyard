using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMissedClockInAlertMinutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MissedClockInAlertMinutes",
                table: "CompanySchedulingSettings",
                type: "integer",
                nullable: false,
                // Companies that already saved their settings get the same 15 minutes as new ones,
                // rather than having missed clock-in alerts silently off.
                defaultValue: 15);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MissedClockInAlertMinutes",
                table: "CompanySchedulingSettings");
        }
    }
}
