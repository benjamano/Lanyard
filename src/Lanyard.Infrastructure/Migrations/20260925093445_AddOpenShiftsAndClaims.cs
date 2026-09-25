using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenShiftsAndClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "Shifts",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateTable(
                name: "LocationSchedulingSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    ClaimsNeedApproval = table.Column<bool>(type: "boolean", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocationSchedulingSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LocationSchedulingSettings_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShiftClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShiftId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ParentClaimId = table.Column<Guid>(type: "uuid", nullable: true),
                    OfferedShiftId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true),
                    RequestedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedByUserId = table.Column<string>(type: "text", nullable: true),
                    DecidedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShiftClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShiftClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShiftClaims_ShiftClaims_ParentClaimId",
                        column: x => x.ParentClaimId,
                        principalTable: "ShiftClaims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShiftClaims_Shifts_OfferedShiftId",
                        column: x => x.OfferedShiftId,
                        principalTable: "Shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShiftClaims_Shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "Shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocationSchedulingSettings_LocationId",
                table: "LocationSchedulingSettings",
                column: "LocationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShiftClaims_OfferedShiftId",
                table: "ShiftClaims",
                column: "OfferedShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_ShiftClaims_ParentClaimId",
                table: "ShiftClaims",
                column: "ParentClaimId");

            migrationBuilder.CreateIndex(
                name: "IX_ShiftClaims_ShiftId_Status",
                table: "ShiftClaims",
                columns: new[] { "ShiftId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ShiftClaims_UserId_Status",
                table: "ShiftClaims",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocationSchedulingSettings");

            migrationBuilder.DropTable(
                name: "ShiftClaims");

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "Shifts",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
