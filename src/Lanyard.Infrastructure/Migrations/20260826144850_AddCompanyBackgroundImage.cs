using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyBackgroundImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BackgroundImageFileId",
                table: "Companies",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Companies_BackgroundImageFileId",
                table: "Companies",
                column: "BackgroundImageFileId");

            migrationBuilder.AddForeignKey(
                name: "FK_Companies_FileMetadata_BackgroundImageFileId",
                table: "Companies",
                column: "BackgroundImageFileId",
                principalTable: "FileMetadata",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Companies_FileMetadata_BackgroundImageFileId",
                table: "Companies");

            migrationBuilder.DropIndex(
                name: "IX_Companies_BackgroundImageFileId",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "BackgroundImageFileId",
                table: "Companies");
        }
    }
}
