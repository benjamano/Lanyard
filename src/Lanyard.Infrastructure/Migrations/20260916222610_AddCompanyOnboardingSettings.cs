using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyOnboardingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanyOnboardingSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    SendWelcomeEmail = table.Column<bool>(type: "boolean", nullable: false),
                    WelcomeEmailSubject = table.Column<string>(type: "text", nullable: true),
                    WelcomeEmailBodyHtml = table.Column<string>(type: "text", nullable: true),
                    AutoAttachStandingDocuments = table.Column<bool>(type: "boolean", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyOnboardingSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompanyOnboardingSettings_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CompanyOnboardingStandingAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    FileMetadataId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyOnboardingStandingAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompanyOnboardingStandingAttachments_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CompanyOnboardingStandingAttachments_FileMetadata_FileMetad~",
                        column: x => x.FileMetadataId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyOnboardingSettings_CompanyId",
                table: "CompanyOnboardingSettings",
                column: "CompanyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyOnboardingStandingAttachments_CompanyId",
                table: "CompanyOnboardingStandingAttachments",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyOnboardingStandingAttachments_FileMetadataId",
                table: "CompanyOnboardingStandingAttachments",
                column: "FileMetadataId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyOnboardingSettings");

            migrationBuilder.DropTable(
                name: "CompanyOnboardingStandingAttachments");
        }
    }
}
