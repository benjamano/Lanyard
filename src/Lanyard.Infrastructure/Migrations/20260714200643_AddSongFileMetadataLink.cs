using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSongFileMetadataLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FileMetadataId",
                table: "Songs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Songs_FileMetadataId",
                table: "Songs",
                column: "FileMetadataId");

            migrationBuilder.AddForeignKey(
                name: "FK_Songs_FileMetadata_FileMetadataId",
                table: "Songs",
                column: "FileMetadataId",
                principalTable: "FileMetadata",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Songs_FileMetadata_FileMetadataId",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Songs_FileMetadataId",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "FileMetadataId",
                table: "Songs");
        }
    }
}
