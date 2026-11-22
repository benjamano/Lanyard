using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationChecklists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Checklists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Days = table.Column<int>(type: "integer", nullable: false),
                    OnlyWhenOpen = table.Column<bool>(type: "boolean", nullable: false),
                    DueRule = table.Column<int>(type: "integer", nullable: false),
                    DueOffsetMinutes = table.Column<int>(type: "integer", nullable: false),
                    DueTime = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    StaffPositionId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateByUserId = table.Column<string>(type: "text", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdateByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Checklists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Checklists_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Checklists_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Checklists_StaffPositions_StaffPositionId",
                        column: x => x.StaffPositionId,
                        principalTable: "StaffPositions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChecklistRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    ChecklistId = table.Column<Guid>(type: "uuid", nullable: false),
                    VenueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    DueUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MissedAlertUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChecklistRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChecklistRuns_Checklists_ChecklistId",
                        column: x => x.ChecklistId,
                        principalTable: "Checklists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChecklistRuns_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChecklistRuns_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChecklistTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChecklistId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Response = table.Column<int>(type: "integer", nullable: false),
                    Unit = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateByUserId = table.Column<string>(type: "text", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdateByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChecklistTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChecklistTasks_Checklists_ChecklistId",
                        column: x => x.ChecklistId,
                        principalTable: "Checklists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChecklistRunItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Response = table.Column<int>(type: "integer", nullable: false),
                    Unit = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChecklistRunItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChecklistRunItems_ChecklistRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "ChecklistRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChecklistRunItems_ChecklistTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "ChecklistTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ChecklistCompletions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    ClockInTerminalId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Reading = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UndoneUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UndoneByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChecklistCompletions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChecklistCompletions_AspNetUsers_UndoneByUserId",
                        column: x => x.UndoneByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChecklistCompletions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChecklistCompletions_ChecklistRunItems_RunItemId",
                        column: x => x.RunItemId,
                        principalTable: "ChecklistRunItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChecklistCompletions_ClockInTerminals_ClockInTerminalId",
                        column: x => x.ClockInTerminalId,
                        principalTable: "ClockInTerminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistCompletions_ClockInTerminalId",
                table: "ChecklistCompletions",
                column: "ClockInTerminalId");

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistCompletions_RunItemId",
                table: "ChecklistCompletions",
                column: "RunItemId",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistCompletions_UndoneByUserId",
                table: "ChecklistCompletions",
                column: "UndoneByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistCompletions_UserId",
                table: "ChecklistCompletions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistRunItems_RunId",
                table: "ChecklistRunItems",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistRunItems_TaskId",
                table: "ChecklistRunItems",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistRuns_ChecklistId_VenueDate",
                table: "ChecklistRuns",
                columns: new[] { "ChecklistId", "VenueDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistRuns_CompanyId",
                table: "ChecklistRuns",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistRuns_LocationId_VenueDate",
                table: "ChecklistRuns",
                columns: new[] { "LocationId", "VenueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Checklists_CompanyId",
                table: "Checklists",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Checklists_LocationId_IsActive",
                table: "Checklists",
                columns: new[] { "LocationId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Checklists_StaffPositionId",
                table: "Checklists",
                column: "StaffPositionId");

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistTasks_ChecklistId_SortOrder",
                table: "ChecklistTasks",
                columns: new[] { "ChecklistId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChecklistCompletions");

            migrationBuilder.DropTable(
                name: "ChecklistRunItems");

            migrationBuilder.DropTable(
                name: "ChecklistRuns");

            migrationBuilder.DropTable(
                name: "ChecklistTasks");

            migrationBuilder.DropTable(
                name: "Checklists");
        }
    }
}
