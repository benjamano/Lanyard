using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RestrictStaffDocumentFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsRestricted",
                table: "FileMetadata",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Staff documents uploaded before this went into the File Manager's root folder as
            // ordinary files, readable by anyone signed in. Hide every one that's already there.
            migrationBuilder.Sql(
                """
                UPDATE "FileMetadata" SET "IsRestricted" = TRUE
                WHERE "Id" IN (SELECT "FileMetadataId" FROM "StaffDocuments");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsRestricted",
                table: "FileMetadata");
        }
    }
}
