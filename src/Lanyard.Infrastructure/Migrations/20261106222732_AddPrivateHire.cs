using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPrivateHire : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HireKind",
                table: "PartyStaffingRules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HireKind",
                table: "PartyBookings",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HireKind",
                table: "PartyStaffingRules");

            migrationBuilder.DropColumn(
                name: "HireKind",
                table: "PartyBookings");
        }
    }
}
