using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceStaffDocumentReminderIntervalsWithPerDocumentReminderDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffDocumentReminderSents");

            migrationBuilder.DropTable(
                name: "StaffDocumentReminderIntervals");

            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderDate",
                table: "StaffDocuments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderSentDate",
                table: "StaffDocuments",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReminderDate",
                table: "StaffDocuments");

            migrationBuilder.DropColumn(
                name: "ReminderSentDate",
                table: "StaffDocuments");

            migrationBuilder.CreateTable(
                name: "StaffDocumentReminderIntervals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffDocumentTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    DaysBeforeExpiry = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffDocumentReminderIntervals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffDocumentReminderIntervals_StaffDocumentTypes_StaffDocu~",
                        column: x => x.StaffDocumentTypeId,
                        principalTable: "StaffDocumentTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StaffDocumentReminderSents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReminderIntervalId = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiryDateSnapshot = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffDocumentReminderSents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffDocumentReminderSents_StaffDocumentReminderIntervals_R~",
                        column: x => x.ReminderIntervalId,
                        principalTable: "StaffDocumentReminderIntervals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StaffDocumentReminderSents_StaffDocuments_StaffDocumentId",
                        column: x => x.StaffDocumentId,
                        principalTable: "StaffDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StaffDocumentReminderIntervals_StaffDocumentTypeId",
                table: "StaffDocumentReminderIntervals",
                column: "StaffDocumentTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffDocumentReminderSents_ReminderIntervalId",
                table: "StaffDocumentReminderSents",
                column: "ReminderIntervalId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffDocumentReminderSents_StaffDocumentId_ReminderInterval~",
                table: "StaffDocumentReminderSents",
                columns: new[] { "StaffDocumentId", "ReminderIntervalId", "ExpiryDateSnapshot" },
                unique: true);
        }
    }
}
