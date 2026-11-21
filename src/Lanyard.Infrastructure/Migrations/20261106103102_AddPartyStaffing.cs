using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyStaffing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PartyBookingId",
                table: "Shifts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PartyStaffingRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    PartyType = table.Column<string>(type: "text", nullable: true),
                    StaffPositionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffCount = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartyStaffingRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartyStaffingRules_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PartyStaffingRules_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PartyStaffingRules_StaffPositions_StaffPositionId",
                        column: x => x.StaffPositionId,
                        principalTable: "StaffPositions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Shifts_PartyBookingId",
                table: "Shifts",
                column: "PartyBookingId");

            migrationBuilder.CreateIndex(
                name: "IX_PartyStaffingRules_CompanyId",
                table: "PartyStaffingRules",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_PartyStaffingRules_LocationId",
                table: "PartyStaffingRules",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_PartyStaffingRules_StaffPositionId",
                table: "PartyStaffingRules",
                column: "StaffPositionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Shifts_PartyBookings_PartyBookingId",
                table: "Shifts",
                column: "PartyBookingId",
                principalTable: "PartyBookings",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Shifts_PartyBookings_PartyBookingId",
                table: "Shifts");

            migrationBuilder.DropTable(
                name: "PartyStaffingRules");

            migrationBuilder.DropIndex(
                name: "IX_Shifts_PartyBookingId",
                table: "Shifts");

            migrationBuilder.DropColumn(
                name: "PartyBookingId",
                table: "Shifts");
        }
    }
}
