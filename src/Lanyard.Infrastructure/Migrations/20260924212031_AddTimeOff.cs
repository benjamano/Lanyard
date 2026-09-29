using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTimeOff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TimeOffTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    IsPaid = table.Column<bool>(type: "boolean", nullable: false),
                    DeductsFromAllowance = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeOffTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimeOffTypes_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TimeOffAllowances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    TimeOffTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffPositionId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    IsUnlimited = table.Column<bool>(type: "boolean", nullable: false),
                    AllowanceHours = table.Column<decimal>(type: "numeric", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeOffAllowances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimeOffAllowances_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TimeOffAllowances_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TimeOffAllowances_StaffPositions_StaffPositionId",
                        column: x => x.StaffPositionId,
                        principalTable: "StaffPositions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TimeOffAllowances_TimeOffTypes_TimeOffTypeId",
                        column: x => x.TimeOffTypeId,
                        principalTable: "TimeOffTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TimeOffRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    TimeOffTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Hours = table.Column<decimal>(type: "numeric", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RequestedDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RequestedByUserId = table.Column<string>(type: "text", nullable: true),
                    DecidedByUserId = table.Column<string>(type: "text", nullable: true),
                    DecidedDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionReason = table.Column<string>(type: "text", nullable: true),
                    CancelledDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeOffRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimeOffRequests_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TimeOffRequests_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimeOffRequests_TimeOffTypes_TimeOffTypeId",
                        column: x => x.TimeOffTypeId,
                        principalTable: "TimeOffTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TimeOffAllowances_CompanyId",
                table: "TimeOffAllowances",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeOffAllowances_StaffPositionId",
                table: "TimeOffAllowances",
                column: "StaffPositionId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeOffAllowances_TimeOffTypeId",
                table: "TimeOffAllowances",
                column: "TimeOffTypeId",
                unique: true,
                filter: "\"StaffPositionId\" IS NULL AND \"UserId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TimeOffAllowances_TimeOffTypeId_StaffPositionId",
                table: "TimeOffAllowances",
                columns: new[] { "TimeOffTypeId", "StaffPositionId" },
                unique: true,
                filter: "\"StaffPositionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TimeOffAllowances_TimeOffTypeId_UserId",
                table: "TimeOffAllowances",
                columns: new[] { "TimeOffTypeId", "UserId" },
                unique: true,
                filter: "\"UserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TimeOffAllowances_UserId",
                table: "TimeOffAllowances",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeOffRequests_LocationId_Status",
                table: "TimeOffRequests",
                columns: new[] { "LocationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TimeOffRequests_TimeOffTypeId",
                table: "TimeOffRequests",
                column: "TimeOffTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeOffRequests_UserId_StartDate",
                table: "TimeOffRequests",
                columns: new[] { "UserId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_TimeOffTypes_CompanyId_Name",
                table: "TimeOffTypes",
                columns: new[] { "CompanyId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TimeOffAllowances");

            migrationBuilder.DropTable(
                name: "TimeOffRequests");

            migrationBuilder.DropTable(
                name: "TimeOffTypes");
        }
    }
}
