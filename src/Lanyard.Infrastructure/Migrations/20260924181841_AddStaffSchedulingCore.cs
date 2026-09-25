using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffSchedulingCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanySchedulingSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    FinancialYearStartMonth = table.Column<int>(type: "integer", nullable: false),
                    FinancialYearStartDay = table.Column<int>(type: "integer", nullable: false),
                    HoursPerDay = table.Column<decimal>(type: "numeric", nullable: false),
                    ShiftReminderLeadHours = table.Column<int>(type: "integer", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanySchedulingSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompanySchedulingSettings_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StaffPositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ColorIndex = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffPositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffPositions_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserClockInPins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    PinHash = table.Column<string>(type: "text", nullable: false),
                    SetDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SetByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserClockInPins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserClockInPins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContractRequirements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    StaffPositionId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    MinShiftsPerWeek = table.Column<int>(type: "integer", nullable: true),
                    MinShiftLengthHours = table.Column<decimal>(type: "numeric", nullable: true),
                    MinHoursPerWeek = table.Column<decimal>(type: "numeric", nullable: true),
                    MaxHoursPerWeek = table.Column<decimal>(type: "numeric", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractRequirements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContractRequirements_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ContractRequirements_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractRequirements_StaffPositions_StaffPositionId",
                        column: x => x.StaffPositionId,
                        principalTable: "StaffPositions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "UserPositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    StaffPositionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserPositions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserPositions_StaffPositions_StaffPositionId",
                        column: x => x.StaffPositionId,
                        principalTable: "StaffPositions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanySchedulingSettings_CompanyId",
                table: "CompanySchedulingSettings",
                column: "CompanyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractRequirements_CompanyId",
                table: "ContractRequirements",
                column: "CompanyId",
                unique: true,
                filter: "\"StaffPositionId\" IS NULL AND \"UserId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ContractRequirements_CompanyId_UserId",
                table: "ContractRequirements",
                columns: new[] { "CompanyId", "UserId" },
                unique: true,
                filter: "\"UserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ContractRequirements_StaffPositionId",
                table: "ContractRequirements",
                column: "StaffPositionId",
                unique: true,
                filter: "\"StaffPositionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ContractRequirements_UserId",
                table: "ContractRequirements",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffPositions_CompanyId_Name",
                table: "StaffPositions",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserClockInPins_UserId",
                table: "UserClockInPins",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserPositions_StaffPositionId",
                table: "UserPositions",
                column: "StaffPositionId");

            migrationBuilder.CreateIndex(
                name: "IX_UserPositions_UserId",
                table: "UserPositions",
                column: "UserId",
                unique: true,
                filter: "\"IsPrimary\"");

            migrationBuilder.CreateIndex(
                name: "IX_UserPositions_UserId_StaffPositionId",
                table: "UserPositions",
                columns: new[] { "UserId", "StaffPositionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanySchedulingSettings");

            migrationBuilder.DropTable(
                name: "ContractRequirements");

            migrationBuilder.DropTable(
                name: "UserClockInPins");

            migrationBuilder.DropTable(
                name: "UserPositions");

            migrationBuilder.DropTable(
                name: "StaffPositions");
        }
    }
}
