using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffDocumentCatalogAndDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StaffDocumentTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    RequiresExpiryDate = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffDocumentTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffDocumentTypes_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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
                name: "StaffDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    StaffDocumentTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileMetadataId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UploadedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UploadedByUserId = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffDocuments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StaffDocuments_FileMetadata_FileMetadataId",
                        column: x => x.FileMetadataId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StaffDocuments_StaffDocumentTypes_StaffDocumentTypeId",
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
                    StaffDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReminderIntervalId = table.Column<Guid>(type: "uuid", nullable: false),
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

            migrationBuilder.CreateIndex(
                name: "IX_StaffDocuments_FileMetadataId",
                table: "StaffDocuments",
                column: "FileMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffDocuments_StaffDocumentTypeId",
                table: "StaffDocuments",
                column: "StaffDocumentTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffDocuments_UserId",
                table: "StaffDocuments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffDocumentTypes_CompanyId_Name",
                table: "StaffDocumentTypes",
                columns: new[] { "CompanyId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffDocumentReminderSents");

            migrationBuilder.DropTable(
                name: "StaffDocumentReminderIntervals");

            migrationBuilder.DropTable(
                name: "StaffDocuments");

            migrationBuilder.DropTable(
                name: "StaffDocumentTypes");
        }
    }
}
