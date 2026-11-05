using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaylistSongPosition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Songs_CompanyId",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_PlaylistSongMembers_PlaylistId",
                table: "PlaylistSongMembers");

            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "PlaylistSongMembers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Keep the order every playlist shows today - newest song first - as its stored order.
            migrationBuilder.Sql("""
                UPDATE "PlaylistSongMembers" AS m
                SET "Position" = ranked.position
                FROM (
                    SELECT psm."SongId", psm."PlaylistId",
                           ROW_NUMBER() OVER (PARTITION BY psm."PlaylistId" ORDER BY s."CreateDate" DESC, s."Name") - 1 AS position
                    FROM "PlaylistSongMembers" psm
                    JOIN "Songs" s ON s."Id" = psm."SongId"
                ) AS ranked
                WHERE m."SongId" = ranked."SongId" AND m."PlaylistId" = ranked."PlaylistId";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Songs_CompanyId_IsActive_Name",
                table: "Songs",
                columns: new[] { "CompanyId", "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistSongMembers_PlaylistId_Position",
                table: "PlaylistSongMembers",
                columns: new[] { "PlaylistId", "Position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Songs_CompanyId_IsActive_Name",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_PlaylistSongMembers_PlaylistId_Position",
                table: "PlaylistSongMembers");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "PlaylistSongMembers");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_CompanyId",
                table: "Songs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistSongMembers_PlaylistId",
                table: "PlaylistSongMembers",
                column: "PlaylistId");
        }
    }
}
