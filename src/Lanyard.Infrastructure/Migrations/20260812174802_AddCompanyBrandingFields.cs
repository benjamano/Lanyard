using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyBrandingFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LogoFileId",
                table: "Companies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThemeColorHex",
                table: "Companies",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Companies_LogoFileId",
                table: "Companies",
                column: "LogoFileId");

            migrationBuilder.AddForeignKey(
                name: "FK_Companies_FileMetadata_LogoFileId",
                table: "Companies",
                column: "LogoFileId",
                principalTable: "FileMetadata",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Companies_FileMetadata_LogoFileId",
                table: "Companies");

            migrationBuilder.DropIndex(
                name: "IX_Companies_LogoFileId",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "LogoFileId",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "ThemeColorHex",
                table: "Companies");
        }
    }
}
