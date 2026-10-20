using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarFeedTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PublishedBreakMinutes",
                table: "Shifts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedEndUtc",
                table: "Shifts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PublishedStaffPositionId",
                table: "Shifts",
                type: "uuid",
                nullable: true);

            // Snapshot every already-published shift as it now stands. A shift edited since its
            // last publish keeps its published start and is given its current length and position
            // - the closest record there is of what the person was told.
            migrationBuilder.Sql("""
                UPDATE "Shifts"
                SET "PublishedStartUtc" = COALESCE("PublishedStartUtc", "StartUtc"),
                    "PublishedEndUtc" = COALESCE("PublishedStartUtc", "StartUtc") + ("EndUtc" - "StartUtc"),
                    "PublishedStaffPositionId" = "StaffPositionId",
                    "PublishedBreakMinutes" = "BreakMinutes"
                WHERE "PublishedDateUtc" IS NOT NULL;
                """);

            migrationBuilder.CreateTable(
                name: "CalendarFeedTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    TokenHint = table.Column<string>(type: "text", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAccessUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarFeedTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarFeedTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CalendarFeedTokens_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarFeedTokens_CompanyId",
                table: "CalendarFeedTokens",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarFeedTokens_TokenHash",
                table: "CalendarFeedTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarFeedTokens_UserId_CompanyId",
                table: "CalendarFeedTokens",
                columns: new[] { "UserId", "CompanyId" },
                unique: true,
                filter: "\"IsActive\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarFeedTokens");

            migrationBuilder.DropColumn(
                name: "PublishedBreakMinutes",
                table: "Shifts");

            migrationBuilder.DropColumn(
                name: "PublishedEndUtc",
                table: "Shifts");

            migrationBuilder.DropColumn(
                name: "PublishedStaffPositionId",
                table: "Shifts");
        }
    }
}
