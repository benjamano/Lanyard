using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFilesLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Folders_CompanyId",
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_FileMetadata_CompanyId",
                table: "FileMetadata");

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "Folders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserId",
                table: "Folders",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSharedWithStaff",
                table: "Folders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "FileMetadata",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserId",
                table: "FileMetadata",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSystemFile",
                table: "FileMetadata",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // An audio clip's sound was uploaded to the File Manager root like any other file. It
            // belongs to the clip (managed on the Audio Clips page), so it stops showing in Files.
            migrationBuilder.Sql("""
                UPDATE "FileMetadata" SET "IsSystemFile" = TRUE
                WHERE "Id" IN (SELECT "FileMetadataId" FROM "AudioClips" WHERE "FileMetadataId" IS NOT NULL);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Folders_CompanyId_ParentFolderId_IsActive",
                table: "Folders",
                columns: new[] { "CompanyId", "ParentFolderId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_FileMetadata_CompanyId_FolderId_IsActive",
                table: "FileMetadata",
                columns: new[] { "CompanyId", "FolderId", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Folders_CompanyId_ParentFolderId_IsActive",
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_FileMetadata_CompanyId_FolderId_IsActive",
                table: "FileMetadata");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "IsSharedWithStaff",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "FileMetadata");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "FileMetadata");

            migrationBuilder.DropColumn(
                name: "IsSystemFile",
                table: "FileMetadata");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_CompanyId",
                table: "Folders",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_FileMetadata_CompanyId",
                table: "FileMetadata",
                column: "CompanyId");
        }
    }
}
