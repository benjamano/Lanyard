using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LanyardApp.Migrations
{
    /// <inheritdoc />
    public partial class makingUserFieldsNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Playlists_AspNetUsers_CreateByUserId",
                table: "Playlists");

            migrationBuilder.DropForeignKey(
                name: "FK_PlaylistSongMembers_AspNetUsers_CreateByUserId",
                table: "PlaylistSongMembers");

            migrationBuilder.AlterColumn<string>(
                name: "CreateByUserId",
                table: "PlaylistSongMembers",
                type: "nvarchar(450)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AlterColumn<string>(
                name: "CreateByUserId",
                table: "Playlists",
                type: "nvarchar(450)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AddForeignKey(
                name: "FK_Playlists_AspNetUsers_CreateByUserId",
                table: "Playlists",
                column: "CreateByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PlaylistSongMembers_AspNetUsers_CreateByUserId",
                table: "PlaylistSongMembers",
                column: "CreateByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Playlists_AspNetUsers_CreateByUserId",
                table: "Playlists");

            migrationBuilder.DropForeignKey(
                name: "FK_PlaylistSongMembers_AspNetUsers_CreateByUserId",
                table: "PlaylistSongMembers");

            migrationBuilder.AlterColumn<string>(
                name: "CreateByUserId",
                table: "PlaylistSongMembers",
                type: "nvarchar(450)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CreateByUserId",
                table: "Playlists",
                type: "nvarchar(450)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Playlists_AspNetUsers_CreateByUserId",
                table: "Playlists",
                column: "CreateByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PlaylistSongMembers_AspNetUsers_CreateByUserId",
                table: "PlaylistSongMembers",
                column: "CreateByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
