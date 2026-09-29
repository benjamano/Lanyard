using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClockInTerminalsAndTimeEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClockInWindowMinutes",
                table: "CompanySchedulingSettings",
                type: "integer",
                nullable: false,
                // Matches CompanySchedulingSettings' own default - 0 would stop anyone at an
                // already-configured company clocking in even a minute before their shift.
                defaultValue: 60);

            migrationBuilder.CreateTable(
                name: "ClockInTerminals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    DeviceTokenHash = table.Column<string>(type: "text", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "text", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClockInTerminals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClockInTerminals_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TimeEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    ShiftId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClockInTerminalId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClockInUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClockOutUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClockInMethod = table.Column<int>(type: "integer", nullable: false),
                    ClockOutMethod = table.Column<int>(type: "integer", nullable: true),
                    NeedsReview = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewReason = table.Column<string>(type: "text", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    ApprovedByUserId = table.Column<string>(type: "text", nullable: true),
                    ApprovedDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateByUserId = table.Column<string>(type: "text", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdateByUserId = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimeEntries_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimeEntries_ClockInTerminals_ClockInTerminalId",
                        column: x => x.ClockInTerminalId,
                        principalTable: "ClockInTerminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TimeEntries_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimeEntries_Shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "Shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClockInTerminals_DeviceTokenHash",
                table: "ClockInTerminals",
                column: "DeviceTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClockInTerminals_LocationId",
                table: "ClockInTerminals",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_ClockInTerminalId",
                table: "TimeEntries",
                column: "ClockInTerminalId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_LocationId_ClockInUtc",
                table: "TimeEntries",
                columns: new[] { "LocationId", "ClockInUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_ShiftId",
                table: "TimeEntries",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_UserId",
                table: "TimeEntries",
                column: "UserId",
                unique: true,
                filter: "\"ClockOutUtc\" IS NULL AND \"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_UserId_ClockInUtc",
                table: "TimeEntries",
                columns: new[] { "UserId", "ClockInUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TimeEntries");

            migrationBuilder.DropTable(
                name: "ClockInTerminals");

            migrationBuilder.DropColumn(
                name: "ClockInWindowMinutes",
                table: "CompanySchedulingSettings");
        }
    }
}
