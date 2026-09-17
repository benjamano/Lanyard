using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RestrictFileMetadataDeleteForDocumentsAndAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CompanyOnboardingStandingAttachments_FileMetadata_FileMetad~",
                table: "CompanyOnboardingStandingAttachments");

            migrationBuilder.DropForeignKey(
                name: "FK_StaffDocuments_FileMetadata_FileMetadataId",
                table: "StaffDocuments");

            migrationBuilder.AddForeignKey(
                name: "FK_CompanyOnboardingStandingAttachments_FileMetadata_FileMetad~",
                table: "CompanyOnboardingStandingAttachments",
                column: "FileMetadataId",
                principalTable: "FileMetadata",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StaffDocuments_FileMetadata_FileMetadataId",
                table: "StaffDocuments",
                column: "FileMetadataId",
                principalTable: "FileMetadata",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CompanyOnboardingStandingAttachments_FileMetadata_FileMetad~",
                table: "CompanyOnboardingStandingAttachments");

            migrationBuilder.DropForeignKey(
                name: "FK_StaffDocuments_FileMetadata_FileMetadataId",
                table: "StaffDocuments");

            migrationBuilder.AddForeignKey(
                name: "FK_CompanyOnboardingStandingAttachments_FileMetadata_FileMetad~",
                table: "CompanyOnboardingStandingAttachments",
                column: "FileMetadataId",
                principalTable: "FileMetadata",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_StaffDocuments_FileMetadata_FileMetadataId",
                table: "StaffDocuments",
                column: "FileMetadataId",
                principalTable: "FileMetadata",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
